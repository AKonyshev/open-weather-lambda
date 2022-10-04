using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Extensions.Caching;
using Amazon;
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

        private readonly string _apiSecretKey;

        private readonly string _openWeatherUnits;

        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>
        {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" }
        };

        /// <summary>
        /// Ctor
        /// </summary>
        public Handler()
        {
            var redisHost = Environment.GetEnvironmentVariable("elasticCacheUrl");
            var redisPort = Environment.GetEnvironmentVariable("elasticCachePort");
            var options = ConfigurationOptions.Parse($"{redisHost}:{redisPort}");

            var redis = ConnectionMultiplexer.Connect(options);
            _elasticCache = redis.GetDatabase();

            var secretsManagerClient = new AmazonSecretsManagerClient(RegionEndpoint.USEast1);
            _secretsManager = new SecretsManagerCache(secretsManagerClient);

            _client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("baseOpenWeatherUrl"));

            var prefixKey = Environment.GetEnvironmentVariable("elasticCacheInstance");
            _key = new RedisKey($"{prefixKey}-CITY");
            _apiSecretKey = Environment.GetEnvironmentVariable("apiSecretKey");
            _openWeatherUnits = Environment.GetEnvironmentVariable("openWeatherUnits");
        }

        /// <summary>
        /// Aws api gateway handler
        /// </summary>
        /// <param name="request">Request from api gateway</param>
        /// <param name="context">Execution context</param>
        /// <returns>Response data</returns>
        public async Task<APIGatewayProxyResponse> GetCurrentWeather(APIGatewayProxyRequest request, ILambdaContext context)
        {
            if (request == null || request.QueryStringParameters == null)
            {
                LogMessage(context, "Processing request failed - Please add queryStringParameter 'city' to your request");
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = "Bad Request. City required query parameter",
                };
            }

            var cityParam = request.QueryStringParameters
                    .FirstOrDefault(x => string.Equals(x.Key, "city", StringComparison.InvariantCultureIgnoreCase));
            if (cityParam.Equals(default) || string.IsNullOrEmpty(cityParam.Value))
            {
                LogMessage(context, "Processing request failed - Please add queryStringParameter 'city' to your request");
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = "Bad Request. City required query parameter",
                };
            }

            var cityName = cityParam.Value;
            try
            {
                var apiKey = await GetOpenWeatherApiKeyAsync(_apiSecretKey);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return new APIGatewayProxyResponse
                    {
                        StatusCode = (int)HttpStatusCode.InternalServerError,
                        Body = $"Open weather api key {_apiSecretKey} not found",
                    };
                }

                LogMessage(context, $"Parameter City to read is: {cityName}");

                string currentWeatherData = await GetCurrentWeatherDataAsync(cityName, apiKey);
                LogMessage(context, "Processing request succeeded.");

                if (string.IsNullOrWhiteSpace(currentWeatherData))
                {
                    return new APIGatewayProxyResponse
                    {
                        StatusCode = (int)HttpStatusCode.NotFound,
                    };
                }

                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = currentWeatherData,
                    Headers = _headers,
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

        /// <summary>
        /// Get or create current weather data from elastic cache
        /// </summary>
        /// <param name="cityName">City name</param>
        /// <param name="openWeatherApiId">Unique API key</param>
        /// <returns>Current weather data</returns>
        private async Task<string> GetCurrentWeatherDataAsync(string cityName, string openWeatherApiId)
        {
            var weatherDataKey = $"{_key}-{cityName}";
            var weatherCacheData = await _elasticCache.StringGetAsync(weatherDataKey);
            if (weatherCacheData.IsNullOrEmpty)
            {
                var geoPosition = await GetOrCreateGeoPositionAsync(cityName, openWeatherApiId);
                if (geoPosition != null)
                {
                    var currentWeatherData = await GetWeatherDataAsync(geoPosition.Value, openWeatherApiId);
                    currentWeatherData.City = cityName;

                    weatherCacheData = JsonSerializer.Serialize(currentWeatherData, _serializeOptions);
                    await _elasticCache.StringSetAsync(weatherDataKey, weatherCacheData, TimeSpan.FromMinutes(1));
                }
                else
                {
                    return null;
                }
            }

            return weatherCacheData;
        }

        /// <summary>
        /// Get or create geographical coordinates by city
        /// </summary>
        /// <param name="cityName">City name</param>
        /// <param name="openWeatherApiId">Unique API key</param>
        /// <returns>Geographical coordinates</returns>
        private async Task<GeoPosition?> GetOrCreateGeoPositionAsync(string cityName, string openWeatherApiId)
        {
            var geoPosition = await _elasticCache.GeoPositionAsync(_key, cityName);
            if (geoPosition == null)
            {
                var queryBuilder = new QueryBuilder();
                queryBuilder.Add("appid", openWeatherApiId);
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

            return geoPosition;
        }

        /// <summary>
        /// Get current weather data using geographical coordinates
        /// </summary>
        /// <param name="geoPosition">Geographical coordinates</param>
        /// <param name="openWeatherApiId">Unique API key</param>
        /// <returns>Current weather data</returns>
        private async Task<CurrentWeatherData> GetWeatherDataAsync(GeoPosition geoPosition, string openWeatherApiId)
        {
            var queryBuilder = new QueryBuilder();
            queryBuilder.Add("appid", openWeatherApiId);
            queryBuilder.Add("lat", geoPosition.Latitude.ToString());
            queryBuilder.Add("lon", geoPosition.Longitude.ToString());
            queryBuilder.Add("units", _openWeatherUnits);

            queryBuilder.ToQueryString();

            using var resp = await _client.GetAsync($"data/2.5/weather{queryBuilder}");
            var weatherDataString = await resp.Content.ReadAsStringAsync();

            var weatherDataRaw = JsonSerializer.Deserialize<OpenWeatherData>(weatherDataString, _serializeOptions);
            var currentWeatherData = new CurrentWeatherData
            {
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
                currentWeatherData.Wind.Direction = DegreesToDirection(weatherDataRaw.Wind.Deg);
            }

            return currentWeatherData;
        }

        /// <summary>
        /// Get open weather api key from aws secret manager
        /// </summary>
        /// <param name="key">Secret key</param>
        /// <returns>Secret value</returns>
        private async Task<string> GetOpenWeatherApiKeyAsync(string key)
        {
            var mySecret = await _secretsManager.GetSecretString(_apiSecretKey);
            if (string.IsNullOrEmpty(mySecret))
            {
                return null;
            }

            var doc = JsonDocument.Parse(mySecret);
            if (doc.RootElement.TryGetProperty("weather-api-key", out var keyProp))
            {
                return keyProp.GetString();
            }
            
            return null;
        }

        /// <summary>
        /// Convert degree to cardinal directions
        /// </summary>
        private static string DegreesToDirection(double degrees)
        {
            string[] directions = { "N", "NE", "E", "SE", "S", "SW", "W", "NW", "N" };
            return directions[(int)Math.Round((degrees % 360) / 45)];
        }

        private void LogMessage(ILambdaContext ctx, string msg)
        {
            ctx.Logger.LogLine($"{ctx.AwsRequestId}:{ctx.FunctionName} - {msg}");
        }
    }
}
