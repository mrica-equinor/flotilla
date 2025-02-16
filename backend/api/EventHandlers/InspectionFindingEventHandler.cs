﻿using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Api.Database.Models;
using Api.Services;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using NCrontab;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Api.EventHandlers
{
    public class InspectionFindingEventHandler(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<InspectionFindingEventHandler> logger
    ) : BackgroundService
    {
        private readonly string _cronExpression = "19 14 * * * ";
        private InspectionFindingService InspectionFindingService =>
            scopeFactory
                .CreateScope()
                .ServiceProvider.GetRequiredService<InspectionFindingService>();
        private readonly TimeSpan _timeSpan = configuration.GetValue<TimeSpan>(
            "InspectionFindingEventHandler:TimeSpan"
        );

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "InspectionFinding EventHandler service started at {time}",
                DateTime.UtcNow
            );

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var nextExecutionTime = CrontabSchedule
                    .Parse(_cronExpression)
                    .GetNextOccurrence(now);
                var delay = nextExecutionTime - now;

                if (delay.TotalMilliseconds > 0)
                    await Task.Delay(delay, stoppingToken);

                var lastReportingTime = DateTime.UtcNow - _timeSpan;

                var inspectionFindings = await InspectionFindingService.RetrieveInspectionFindings(
                    lastReportingTime,
                    readOnly: true
                );
                logger.LogInformation(
                    "Found {count} inspection findings in last {interval}",
                    inspectionFindings.Count,
                    _timeSpan
                );

                if (inspectionFindings.Count > 0)
                {
                    var findingsList = await GenerateFindingsList(inspectionFindings);
                    string adaptiveCardJson = GenerateAdaptiveCard(
                        $"Rapport {DateTime.UtcNow:yyyy-MM-dd HH}",
                        inspectionFindings.Count,
                        findingsList
                    );
                    string url = GetWebhookURL(configuration, "TeamsInspectionFindingsWebhook");

                    var client = new HttpClient();
                    client.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json")
                    );

                    var content = new StringContent(
                        adaptiveCardJson,
                        Encoding.UTF8,
                        "application/json"
                    );
                    var response = await client.PostAsync(url, content, stoppingToken);

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(stoppingToken);
                        logger.LogWarning(
                            "Webhook request failed with status code {statusCode}. Response body: {errorBody}",
                            response.StatusCode,
                            errorBody
                        );
                    }
                }
            }
        }

        private async Task<List<Finding>> GenerateFindingsList(
            List<InspectionFinding> inspectionFindings
        )
        {
            var findingsList = new List<Finding>();

            foreach (var inspectionFinding in inspectionFindings)
            {
                var missionRun = await InspectionFindingService.GetMissionRunByIsarInspectionId(
                    inspectionFinding.IsarTaskId,
                    readOnly: true
                );
                var task = await InspectionFindingService.GetMissionTaskByIsarInspectionId(
                    inspectionFinding.IsarTaskId,
                    readOnly: true
                );

                if (task != null && missionRun != null)
                {
                    var finding = new Finding(
                        task.TagId ?? "NA",
                        missionRun.InspectionArea?.Plant.Name ?? "NA",
                        missionRun.InspectionArea?.Name ?? "NA",
                        inspectionFinding.Finding,
                        inspectionFinding.InspectionDate
                    );

                    findingsList.Add(finding);
                }
            }
            return findingsList;
        }

        public static string GenerateAdaptiveCard(
            string title,
            int numberOfFindings,
            List<Finding> findingsReports
        )
        {
            var findingsJsonArray = new JArray();

            foreach (var finding in findingsReports)
            {
                var factsArray = new JArray(
                    new JObject(
                        new JProperty("name", "Anlegg"),
                        new JProperty("value", finding.PlantName)
                    ),
                    new JObject(
                        new JProperty("name", "Område"),
                        new JProperty("value", finding.InspectionAreaName)
                    ),
                    new JObject(
                        new JProperty("name", "Tag Number"),
                        new JProperty("value", finding.TagId)
                    ),
                    new JObject(
                        new JProperty("name", "Beskrivelse"),
                        new JProperty("value", finding.FindingDescription)
                    ),
                    new JObject(
                        new JProperty("name", "Tidspunkt"),
                        new JProperty(
                            "value",
                            finding.Timestamp.ToString(
                                "yyyy-MM-dd HH:mm:ss",
                                CultureInfo.InvariantCulture
                            )
                        )
                    )
                );

                var findingObj = new JObject(
                    new JProperty("activityTitle", $"Finding ID: \"{finding.TagId}\""),
                    new JProperty("facts", factsArray)
                );
                findingsJsonArray.Add(findingObj);
            }

            var sections = new JArray(
                new JObject(
                    new JProperty(
                        "activityTitle",
                        $"Inspection report for \"{findingsReports[0].PlantName}\""
                    ),
                    new JProperty(
                        "activitySubtitle",
                        $"Generated on: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"
                    ),
                    new JProperty(
                        "facts",
                        new JArray(
                            new JObject(
                                new JProperty("name", "Number of findings:"),
                                new JProperty("value", numberOfFindings)
                            )
                        )
                    )
                ),
                new JObject(
                    new JProperty(
                        "activityTitle",
                        "The following inspection findings were identified:"
                    )
                )
            );

            foreach (var findingObj in findingsJsonArray)
                sections.Add(findingObj);

            var adaptiveCardObj = new JObject(
                new JProperty("summary", "Inspection Findings Report"),
                new JProperty("themeColor", "0078D7"),
                new JProperty("title", $"Inspection Findings: \"{title}\""),
                new JProperty("sections", sections)
            );

            return adaptiveCardObj.ToString(Formatting.Indented);
        }

        public static string GetWebhookURL(IConfiguration configuration, string secretName)
        {
            string? keyVaultUri =
                configuration.GetSection("KeyVault")["VaultUri"]
                ?? throw new KeyNotFoundException("No key vault in config");

            var keyVault = new SecretClient(
                new Uri(keyVaultUri),
                new DefaultAzureCredential(
                    new DefaultAzureCredentialOptions { ExcludeSharedTokenCacheCredential = true }
                )
            );

            string webhookURL = keyVault.GetSecret(secretName).Value.Value;

            return webhookURL;
        }
    }

    public class Finding(
        string tagId,
        string plantName,
        string inspectionAreaName,
        string findingDescription,
        DateTime timestamp
    )
    {
        public string TagId { get; set; } = tagId;
        public string PlantName { get; set; } = plantName;
        public string InspectionAreaName { get; set; } = inspectionAreaName;
        public string FindingDescription { get; set; } = findingDescription;
        public DateTime Timestamp { get; set; } = timestamp;
    }
}
