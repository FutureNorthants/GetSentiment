using System.Collections.Generic;
using Amazon.Lambda.Core;
using System.Diagnostics;
using System;
using Amazon;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using System.Text;
using System.Net;
using System.Text.Json;
using Amazon.Comprehend;
using Amazon.Comprehend.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace GetSentiment
{
    public class Function
    {
        private static readonly RegionEndpoint primaryRegion = RegionEndpoint.EUWest2;
        private static readonly String secretName = "nbcGlobal";
        private static readonly String secretAlias = "AWSCURRENT";

        private static String caseReference;
        private static String taskToken;
        private static String cxmEndPoint;
        private static String cxmAPIKey;
        private Secrets secrets;
        private CaseDetails caseDetails;


        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            if (await GetSecrets())
            {
                String instance = Environment.GetEnvironmentVariable("instance");
                JObject o = JObject.Parse(input.ToString());
                caseReference = (string)o.SelectToken("CaseReference");
                taskToken = (string)o.SelectToken("TaskToken");
                switch (instance.ToLower())
                {
                    case "live":
                        cxmEndPoint = secrets.cxmEndPointLive;
                        cxmAPIKey = secrets.cxmAPIKeyLive;
                        caseDetails= await GetCustomerContactAsync();
                        Sentiment sentimentLive = await GetSentimentFromAWSAsync(caseDetails.customerContact);
                        if (await UpdateCaseDetailsAsync(sentimentLive))
                        {
                            await SendSuccessAsync();
                        }
                        break;
                    case "test":
                        cxmEndPoint = secrets.cxmEndPointTest;
                        cxmAPIKey = secrets.cxmAPIKeyTest;
                        caseDetails = await GetCustomerContactAsync();
                        Sentiment sentimentTest = await GetSentimentFromAWSAsync(caseDetails.customerContact);
                        if (await UpdateCaseDetailsAsync(sentimentTest))
                        {
                            await SendSuccessAsync();
                        }
                        break;
                    default:
                        await SendFailureAsync("Instance not Live or Test : " + instance.ToLower(), "Lambda Parameter Error");
                        Console.WriteLine("ERROR : Instance not Live or Test : " + instance.ToLower());
                        break;
                }
            }
        }

        private async Task<Boolean> UpdateCaseDetailsAsync(Sentiment sentiment)
        {
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            String uri = "/api/service-api/norbert/case/" + caseReference + "/edit?key=" + cxmAPIKey;
            Dictionary<string, string> cxmPayload = new Dictionary<string, string>
            {
                { "sentiment", sentiment.sentimentRating },
                { "sentiment-score-mixed", sentiment.sentimentMixed },
                { "sentiment-score-negative", sentiment.sentimentNegative },
                { "sentiment-score-neutral", sentiment.sentimentNeutral },
                { "sentiment-score-positive", sentiment.sentimentPositive }
            };
            String json = JsonConvert.SerializeObject(cxmPayload, Formatting.Indented);
            StringContent content = new StringContent(json);

            HttpResponseMessage response = await cxmClient.PatchAsync(uri, content);

            try
            {
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync(error.Message, "UpdateCaseDetailsAsync");
                Console.WriteLine("ERROR : UpdateCaseDetailsAsync :  " + error.Message);
                return false;
            }
        }

        private async Task<Boolean> GetSecrets()
        {
            IAmazonSecretsManager client = new AmazonSecretsManagerClient(primaryRegion);

            GetSecretValueRequest request = new GetSecretValueRequest();
            request.SecretId = secretName;
            request.VersionStage = secretAlias;

            try
            {
                GetSecretValueResponse response = await client.GetSecretValueAsync(request);
                secrets = JsonConvert.DeserializeObject<Secrets>(response.SecretString);
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync("GetSecrets", error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.StackTrace);
                return false;
            }
        }


        private async Task<CaseDetails> GetCustomerContactAsync()
        {
            CaseDetails caseDetails = new CaseDetails();
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            String requestParameters = "key=" + cxmAPIKey;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/service-api/norbert/case/" + caseReference + "?" + requestParameters);
            try
            {
                HttpResponseMessage response = cxmClient.SendAsync(request).Result;
                if (response.IsSuccessStatusCode)
                {
                    HttpContent responseContent = response.Content;
                    String responseString = responseContent.ReadAsStringAsync().Result;
                    JObject caseSearch = JObject.Parse(responseString);
                    caseDetails.customerContact = (String)caseSearch.SelectToken("values.enquiry_details");
                }
                else
                {
                    await SendFailureAsync("Getting case details for " + caseReference, response.StatusCode.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + request.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + response.StatusCode.ToString());
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync("Getting case details for " + caseReference, error.Message);
                Console.WriteLine("ERROR : GetStaffResponseAsync : " + error.StackTrace);
            }
            return caseDetails;
        }

        private async Task<Sentiment> GetSentimentFromAWSAsync(String customerContact)
        {
            Sentiment caseSentiment = new Sentiment();
            try
            {
                AmazonComprehendClient comprehendClient = new AmazonComprehendClient(RegionEndpoint.EUWest2);

                Console.WriteLine("Calling DetectSentiment");
                DetectSentimentRequest detectSentimentRequest = new DetectSentimentRequest()
                {
                    Text = customerContact,
                    LanguageCode = "en"
                };
                DetectSentimentResponse detectSentimentResponse = await comprehendClient.DetectSentimentAsync(detectSentimentRequest);
                caseSentiment.success = true;
                caseSentiment.sentimentRating = detectSentimentResponse.Sentiment;
                caseSentiment.sentimentMixed = ((int)(detectSentimentResponse.SentimentScore.Mixed*100)).ToString();
                caseSentiment.sentimentNegative = ((int)(detectSentimentResponse.SentimentScore.Negative * 100)).ToString();
                caseSentiment.sentimentNeutral = ((int)(detectSentimentResponse.SentimentScore.Neutral * 100)).ToString();
                caseSentiment.sentimentPositive = ((int)(detectSentimentResponse.SentimentScore.Positive * 100)).ToString();
                return caseSentiment;
            }
            catch (Exception error)
            {
                caseSentiment.success = false;
                await SendFailureAsync("Getting Sentiment", error.Message);
                Console.WriteLine("ERROR : GetSentimentFromAWSAsync : " + error.StackTrace);
                return caseSentiment;
            }
        }

        private async Task SendSuccessAsync()
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Success"  },
                { "Message" , "Completed"}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.Message);
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        private async Task SendFailureAsync(String failureCause, String failureError)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskFailureRequest failureRequest = new SendTaskFailureRequest();
            failureRequest.Cause = failureCause;
            failureRequest.Error = failureError;
            failureRequest.TaskToken = taskToken;

            try
            {
                await client.SendTaskFailureAsync(failureRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendFailureAsync : " + error.Message);
                Console.WriteLine("ERROR : SendFailureAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

    }
    class CaseDetails
    {
        public String customerContact { get; set; } = "";
    }

    public class Sentiment
    {
        public Boolean success { get; set; }
        public String sentimentRating { get; set; }
        public String sentimentMixed { get; set; }
        public String sentimentNegative { get; set; }
        public String sentimentNeutral { get; set; }
        public String sentimentPositive { get; set; }
    }

    public class Secrets
    {
        public String cxmEndPointTest { get; set; }
        public String cxmEndPointLive { get; set; }
        public String cxmAPIKeyTest { get; set; }
        public String cxmAPIKeyLive { get; set; }
    }
}