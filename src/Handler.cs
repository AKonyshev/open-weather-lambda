using System;
using System.Collections.Generic;
using System.Net;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Extensions.Caching;
using Amazon;
using Microsoft.Extensions.Caching.Distributed;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.AspNetCore.Http.Extensions;
using StackExchange.Redis;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace OpenWeatherMap
{
    public class Handler
    {
        private readonly IDatabase _elasticCache;

        private readonly JsonSerializerOptions _serializeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly RedisKey _key;

        private readonly SecretsManagerCache _secretsManager;

        private readonly HttpClient _client = new HttpClient();

        private static string ApiSecretKey;

        private static string OpenWeatherApiId;

        public Handler()
        {
            ApiSecretKey = Environment.GetEnvironmentVariable("apiSecretKey");
            OpenWeatherApiId = Environment.GetEnvironmentVariable("openWeatherApiId");

            var redisConfiguration = Environment.GetEnvironmentVariable("elasticCacheUrl");
            var prefixKey = Environment.GetEnvironmentVariable("elasticCacheInstance");
            var options = ConfigurationOptions.Parse($"{redisConfiguration}:6379");

            var redis = ConnectionMultiplexer.Connect(options);
            _elasticCache = redis.GetDatabase();

            var client = new AmazonSecretsManagerClient(RegionEndpoint.USEast1);
            _secretsManager = new SecretsManagerCache(client);

            _client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("baseOpenWeatherUrl"));
            _key = new RedisKey($"{prefixKey}-CITY");
        }

        public async Task<APIGatewayProxyResponse> GetCurrentWeather(APIGatewayProxyRequest request, ILambdaContext context)
        {
            if (request != null && request.QueryStringParameters.TryGetValue("city", out var cityName))
            {
                try
                {
                    LogMessage(context, $"Parameter City to read is: {cityName}");
                    var result = await GetCurrentWeatherData(cityName);
                    LogMessage(context, "Processing request succeeded.");

                    // {"weather-api-key":"0d1a332b0a179826a3763b51312e9863"}
                    var mySecret = await _secretsManager.GetSecretString(ApiSecretKey);
                    return new APIGatewayProxyResponse
                    {
                       StatusCode = (int)HttpStatusCode.OK,
                       Body = result,
                       Headers = new Dictionary<string, string>
                       {
                            { "Content-Type", "application/json" },
                            { "Access-Control-Allow-Origin", "*" }
                       }
                    };
                }
                catch (Exception ex)
                {
                    LogMessage(context, $"Processing request failed - {ex.Message}");
                    return new APIGatewayProxyResponse
                    {
                        StatusCode = (int)HttpStatusCode.InternalServerError,
                        Body = "Internal server error",
                    };
                }
            }
            else
            {
                LogMessage(context, "Processing request failed - Please add queryStringParameter 'city' to your request");
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = "Bad Request. City required query parameter",
                };
            }
        }

        private async Task<string> GetCurrentWeatherData(string cityName)
        {
            var weatherDataKey = $"{_key}-{cityName}";
            var weatherData = await _elasticCache.StringGetAsync(weatherDataKey);
            if (weatherData.IsNullOrEmpty)
            {
                var geoPosition = await _elasticCache.GeoPositionAsync(_key, cityName);
                if (geoPosition == null)
                {
                    var queryBuilder = new QueryBuilder();
                    queryBuilder.Add("appid", OpenWeatherApiId);
                    queryBuilder.Add("q", cityName);

                    queryBuilder.ToQueryString();

                    using var resp = await _client.GetAsync($"geo/1.0/direct{queryBuilder}");
                    var geoResultString = await resp.Content.ReadAsStringAsync();

                    var geoResults = JsonSerializer.Deserialize<OpenGeographicalCoordinates[]>(geoResultString, _serializeOptions);
                    if (geoResults != null && geoResults.Length > 0)
                    {
                        var cityCoordinates = geoResults[0];

                        var geoEntry = new GeoEntry(cityCoordinates.Lon, cityCoordinates.Lat, cityCoordinates.Name);
                        await _elasticCache.GeoAddAsync(_key, geoEntry);

                        geoPosition = geoEntry.Position;
                    }
                    else
                    {
                        geoPosition = null;
                    }
                }

                if (geoPosition != null)
                {
                    var queryBuilder = new QueryBuilder();
                    queryBuilder.Add("appid", OpenWeatherApiId);
                    queryBuilder.Add("lat", geoPosition.Value.Latitude.ToString());
                    queryBuilder.Add("lon", geoPosition.Value.Longitude.ToString());

                    queryBuilder.ToQueryString();

                    using var resp = await _client.GetAsync($"data/2.5/weather{queryBuilder}");
                    var weatherDataString = await resp.Content.ReadAsStringAsync();

                    var weatherDataRaw = JsonSerializer.Deserialize<OpenWeatherData>(weatherDataString, _serializeOptions);
                    var currentWeatherData = new CurrentWeatherData
                    {
                        City = weatherDataRaw.Name,
                        WeatherCondition = new WeatherConditionBlock(),
                        Wind = new WindBlock(),
                    };

                    if (weatherDataRaw.Weather != null && weatherDataRaw.Weather.Length > 0)
                    {
                        var firstWeather = weatherDataRaw.Weather[0];
                        currentWeatherData.WeatherCondition.Type = firstWeather.Main;
                    }

                    if (weatherDataRaw.Main != null)
                    {
                        currentWeatherData.Temperature = weatherDataRaw.Main.Temp;
                        currentWeatherData.WeatherCondition.Pressure = weatherDataRaw.Main.Pressure;
                        currentWeatherData.WeatherCondition.Humidity = weatherDataRaw.Main.Humidity;
                    }

                    if (weatherDataRaw.Wind != null)
                    {
                        currentWeatherData.Wind.Speed = weatherDataRaw.Wind.Speed;
                        currentWeatherData.Wind.Direction = DegreesToCardinal(weatherDataRaw.Wind.Deg);
                    }

                    weatherData = JsonSerializer.Serialize(currentWeatherData, _serializeOptions);
                    await _elasticCache.StringSetAsync(weatherDataKey, weatherData, TimeSpan.FromMinutes(1));

                    return weatherData;
                }
                else
                {
                    return null;
                }
            }

            return weatherData;
        }

        private void LogMessage(ILambdaContext ctx, string msg)
        {
            ctx.Logger.LogLine($"{ctx.AwsRequestId}:{ctx.FunctionName} - {msg}");
        }

        private APIGatewayProxyResponse CreateResponse(IDictionary<string, string> result)
        {
            int statusCode = (result != null) ?
                (int)HttpStatusCode.OK :
                (int)HttpStatusCode.InternalServerError;

            string body = (result != null) ? JsonSerializer.Serialize(result) : string.Empty;
            var response = new APIGatewayProxyResponse
            {
                StatusCode = statusCode,
                Body = body,
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Access-Control-Allow-Origin", "*" }
                }
            };

            return response;
        }

        private static string DegreesToCardinal(double degrees)
        {
            string[] caridnals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW", "N" };
            return caridnals[(int)Math.Round((degrees % 360) / 45)];
        }
    }
}
