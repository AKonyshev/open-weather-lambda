using System;
using System.Collections.Generic;
using System.Net;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Extensions.Caching;
using Amazon.SecretsManager.Model;
using Amazon;
using Microsoft.Extensions.Caching.Distributed;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.StackExchangeRedis;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace OpenWeatherMap
{
    public class Handler
    {
        private readonly IDistributedCache _elasticCache;
        private readonly SecretsManagerCache _secretsManager;
        public Handler()
        {
            var serviceCollection = new ServiceCollection();
            var cacheOptions = new RedisCacheOptions{
                Configuration = "redis.5vv4vd.ng.0001.use1.cache.amazonaws.com",
                InstanceName = "DEVS-547",
            };

            _elasticCache = new RedisCache(cacheOptions);

            var client = new AmazonSecretsManagerClient(RegionEndpoint.USEast1);
            _secretsManager = new SecretsManagerCache(client);
        }

        public async Task<APIGatewayProxyResponse> GetCurrentWeather(APIGatewayProxyRequest request, ILambdaContext context)
        {
            APIGatewayProxyResponse response;
            if (request != null && request.QueryStringParameters.Count > 0)
            {
                try
                {
                    string secretName = "prod/weather/api";

                    var mySecret = await _secretsManager.GetSecretString(secretName);

                    var key = "weather-key";
                    var result = await _elasticCache.GetStringAsync(key);

                    if (string.IsNullOrWhiteSpace(result))
                    {
                       await _elasticCache.SetStringAsync(key, "test");
                    }

                    result = await _elasticCache.GetStringAsync(key);

                    return new APIGatewayProxyResponse
                    {
                       StatusCode = (int)HttpStatusCode.OK,
                       Body = $"redis: {result} api-key: {mySecret}",
                    };

                    // var result = processor.CurrentTimeUTC();
                    response = CreateResponse(request.QueryStringParameters);
                    LogMessage(context, "First Parameter Value to read is: " + request.QueryStringParameters["foo"]);
                    LogMessage(context, "Processing request succeeded.");
                }
                catch (Exception ex)
                {
                    LogMessage(context, string.Format("Processing request failed - {0}", ex.Message));
                    return new APIGatewayProxyResponse
                    {
                        StatusCode = (int)HttpStatusCode.InternalServerError,
                        Body = "Internal server error",
                    };
                }
            }
            else
            {
                LogMessage(context, "Processing request failed - Please add queryStringParameter 'foo' to your request - see sample in readme");
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = "Bad Request. City required query parameter",
                };
            }

            return response;
        }

        void LogMessage(ILambdaContext ctx, string msg)
        {
            ctx.Logger.LogLine(
                string.Format("{0}:{1} - {2}",
                    ctx.AwsRequestId,
                    ctx.FunctionName,
                    msg));
        }

        APIGatewayProxyResponse CreateResponse(IDictionary<string, string> result)
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
    }
}
