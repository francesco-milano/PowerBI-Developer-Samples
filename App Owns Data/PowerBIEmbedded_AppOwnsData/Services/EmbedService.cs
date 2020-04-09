using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.PowerBI.Api;
using Microsoft.PowerBI.Api.Models;
using Microsoft.Rest;
using PowerBIEmbedded_AppOwnsData.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace PowerBIEmbedded_AppOwnsData.Models
{
    public partial class EmbedService : IEmbedService
    {
        private static readonly string AuthorityUrl = ConfigurationManager.AppSettings["authorityUrl"];
        private static readonly string ResourceUrl = ConfigurationManager.AppSettings["resourceUrl"];
        private static readonly string ApplicationId = ConfigurationManager.AppSettings["applicationId"];
        private static readonly string ApiUrl = ConfigurationManager.AppSettings["apiUrl"];
        private static readonly string WorkspaceId = ConfigurationManager.AppSettings["workspaceId"];
        private static readonly string ReportId = ConfigurationManager.AppSettings["reportId"];

        private static readonly string AuthenticationType = ConfigurationManager.AppSettings["AuthenticationType"];
        private static readonly NameValueCollection sectionConfig = ConfigurationManager.GetSection(AuthenticationType) as NameValueCollection;
        private static readonly string ApplicationSecret = sectionConfig["applicationSecret"];
        private static readonly string Tenant = sectionConfig["tenant"];
        private static readonly string Username = sectionConfig["pbiUsername"];
        private static readonly string Password = sectionConfig["pbiPassword"];

        public ExportConfig ExportConfig
        {
            get { return m_exportConfig; }
        }

        public EmbedConfig EmbedConfig
        {
            get { return m_embedConfig; }
        }

        public TileEmbedConfig TileEmbedConfig
        {
            get { return m_tileEmbedConfig; }
        }

        private ExportConfig m_exportConfig;
        private EmbedConfig m_embedConfig;
        private TileEmbedConfig m_tileEmbedConfig;
        private TokenCredentials m_tokenCredentials;

        public EmbedService()
        {
            m_tokenCredentials = null;
            m_exportConfig = new ExportConfig();
            m_embedConfig = new EmbedConfig();
            m_tileEmbedConfig = new TileEmbedConfig();
        }

        /////// Export sample ///////
        private async Task<string> PostExportRequest(
            PowerBIClient client,
            Guid reportId,
            Guid groupId,
            FileFormat format,
            IList<string> pageNames = null /* Get the page names from the GetPages API */)
        {
            var powerBIReportExportConfiguration = new PowerBIReportExportConfiguration
            {
                Settings = new ExportReportSettings
                {
                    Locale = "en-us",
                },
                // Note that page names differ from the page display names.
                // To get the page names use the GetPages API.
                Pages = pageNames?.Select(pn => new ExportReportPage(pageName: pn)).ToList(),
            };
            var exportRequest = new ExportReportRequest
            {
                Format = format,
                PowerBIReportConfiguration = powerBIReportExportConfiguration,
            };
            var export = await client.Reports.ExportToFileInGroupAsync(groupId, reportId, exportRequest);
            // Save the export ID, you'll need it for polling and getting the exported file
            return export.Id;
        }

        private async Task<Export> PollExportRequest(
            PowerBIClient client,
            Guid reportId,
            Guid groupId,
            string exportId /* Get from the ExportToAsync response */,
            int timeOutInMinutes,
            CancellationToken token)
        {
            Export exportStatus = null;
            DateTime startTime = DateTime.UtcNow;
            const int c_secToMillisec = 1000;
            do
            {
                if (DateTime.UtcNow.Subtract(startTime).TotalMinutes > timeOutInMinutes || token.IsCancellationRequested)
                {
                    // Error handling for timeout and cancellations 
                    return null;
                }
                var httpMessage = await client.Reports.GetExportToFileStatusInGroupWithHttpMessagesAsync(groupId, reportId, exportId);
                exportStatus = httpMessage.Body;
                // You can track the export progress using the PercentComplete that's part of the response
                //SomeTextBox.Text = string.Format("{0} (Percent Complete : {1}%)", exportStatus.Status.ToString(), exportStatus.PercentComplete);
                if (exportStatus.Status == ExportState.Running || exportStatus.Status == ExportState.NotStarted)
                {
                    // The recommended waiting time between polling requests can be found in the RetryAfter header
                    // Note that this header is only populated when the status is either Running or NotStarted
                    var retryAfter = httpMessage.Response.Headers.RetryAfter;
                    var retryAfterInSec = retryAfter.Delta.Value.Seconds;
                    await Task.Delay(retryAfterInSec * c_secToMillisec);
                }
            }
            // While not in a terminal state, keep polling
            while (exportStatus.Status != ExportState.Succeeded && exportStatus.Status != ExportState.Failed);
            return exportStatus;
        }

        private async Task<ExportedFile> GetExportedFile(
            PowerBIClient client,
            Guid reportId,
            Guid groupId,
            Export export /* Get from the GetExportStatusAsync response */)
        {
            if (export.Status == ExportState.Succeeded)
            {
                var fileStream = await client.Reports.GetFileOfExportToFileAsync(groupId, reportId, export.Id);
                return new ExportedFile
                {
                    FileStream = fileStream,
                    FileSuffix = export.ResourceFileExtension,
                };
            }
            return null;
        }

        private async Task<ExportedFile> ExportPowerBIReport(
            PowerBIClient client,
            Guid reportId,
            Guid groupId,
            FileFormat format,
            int pollingtimeOutInMinutes,
            CancellationToken token,
            IList<string> pageNames = null /* Get the page names from the GetPages API */)
        {
            try
            {
                var exportId = await PostExportRequest(client, reportId, groupId, format, pageNames);
                var export = await PollExportRequest(client, reportId, groupId, exportId, pollingtimeOutInMinutes, token);
                if (export == null || export.Status != ExportState.Succeeded)
                {
                    // Error, failure in exporting the report
                    return null;
                }
                return await GetExportedFile(client, reportId, groupId, export);
            }
            catch
            {
                // Error handling
                throw;
            }
        }

        public async Task<bool> ExportReport()
        {
            // Get token credentials for user
            var getCredentialsResult = await GetTokenCredentials();
            if (!getCredentialsResult)
            {
                // The error message set in GetTokenCredentials
                return false;
            }

            try
            {
                // Create a Power BI Client object. It will be used to call Power BI APIs.
                using (var client = new PowerBIClient(new Uri(ApiUrl), m_tokenCredentials))
                {
                    // Get a list of reports.
                    var reports = await client.Reports.GetReportsInGroupAsync(new Guid(WorkspaceId));

                    // No reports retrieved for the given workspace.
                    if (reports.Value.Count() == 0)
                    {
                        m_embedConfig.ErrorMessage = "No reports were found in the workspace";
                        return false;
                    }

                    Report report;
                    if (string.IsNullOrWhiteSpace(ReportId))
                    {
                        // Get the first report in the workspace.
                        report = reports.Value.FirstOrDefault();
                    }
                    else
                    {
                        report = reports.Value.FirstOrDefault(r => r.Id.ToString().Equals(ReportId, StringComparison.InvariantCultureIgnoreCase));
                    }

                    if (report == null)
                    {
                        m_embedConfig.ErrorMessage = "No report with the given ID was found in the workspace. Make sure ReportId is valid.";
                        return false;
                    }

                    CancellationToken token = new CancellationToken();

                    ExportedFile file = await ExportPowerBIReport(
                        client,
                        report.Id,
                        new Guid(WorkspaceId),
                        FileFormat.PPTX,
                        3,
                        token
                        );

                    m_exportConfig.File = file;
                    m_exportConfig.FileName = String.Concat(report.Name, file.FileSuffix);
                }
            }
            catch (HttpOperationException exc)
            {
                m_exportConfig.ErrorMessage = string.Format("Status: {0} ({1})\r\nResponse: {2}\r\nRequestId: {3}", exc.Response.StatusCode, (int)exc.Response.StatusCode, exc.Response.Content, exc.Response.Headers["RequestId"].FirstOrDefault());
                return false;
            }

            return true;
        }

        public async Task<bool> EmbedReport(string username, string roles)
        {
            
            // Get token credentials for user
            var getCredentialsResult = await GetTokenCredentials();
            if (!getCredentialsResult)
            {
                // The error message set in GetTokenCredentials
                return false;
            }

            try
            {
                // Create a Power BI Client object. It will be used to call Power BI APIs.
                using (var client = new PowerBIClient(new Uri(ApiUrl), m_tokenCredentials))
                {
                    // Get a list of reports.
                    var reports = await client.Reports.GetReportsInGroupAsync(new Guid(WorkspaceId));

                    // No reports retrieved for the given workspace.
                    if (reports.Value.Count() == 0)
                    {
                        m_embedConfig.ErrorMessage = "No reports were found in the workspace";
                        return false;
                    }

                    Report report;
                    if (string.IsNullOrWhiteSpace(ReportId))
                    {
                        // Get the first report in the workspace.
                        report = reports.Value.FirstOrDefault();
                    }
                    else
                    {
                        report = reports.Value.FirstOrDefault(r => r.Id.ToString().Equals(ReportId, StringComparison.InvariantCultureIgnoreCase));
                    }

                    if (report == null)
                    {
                        m_embedConfig.ErrorMessage = "No report with the given ID was found in the workspace. Make sure ReportId is valid.";
                        return false;
                    }

                    var datasets = await client.Datasets.GetDatasetInGroupAsync(new Guid(WorkspaceId), report.DatasetId);
                    m_embedConfig.IsEffectiveIdentityRequired = datasets.IsEffectiveIdentityRequired;
                    m_embedConfig.IsEffectiveIdentityRolesRequired = datasets.IsEffectiveIdentityRolesRequired;
                    GenerateTokenRequest generateTokenRequestParameters;
                    // This is how you create embed token with effective identities
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        var rls = new EffectiveIdentity(username, new List<string> { report.DatasetId });
                        if (!string.IsNullOrWhiteSpace(roles))
                        {
                            var rolesList = new List<string>();
                            rolesList.AddRange(roles.Split(','));
                            rls.Roles = rolesList;
                        }
                        // Generate Embed Token with effective identities.
                        generateTokenRequestParameters = new GenerateTokenRequest(accessLevel: "view", identities: new List<EffectiveIdentity> { rls });
                    }
                    else
                    {
                        // Generate Embed Token for reports without effective identities.
                        generateTokenRequestParameters = new GenerateTokenRequest(accessLevel: "view");
                    }

                    var tokenResponse = await client.Reports.GenerateTokenInGroupAsync(new Guid(WorkspaceId), report.Id, generateTokenRequestParameters);

                    if (tokenResponse == null)
                    {
                        m_embedConfig.ErrorMessage = "Failed to generate embed token.";
                        return false;
                    }

                    // Generate Embed Configuration.
                    m_embedConfig.EmbedToken = tokenResponse;
                    m_embedConfig.EmbedUrl = report.EmbedUrl;
                    m_embedConfig.Id = report.Id.ToString();
                }
            }
            catch (HttpOperationException exc)
            {
                m_embedConfig.ErrorMessage = string.Format("Status: {0} ({1})\r\nResponse: {2}\r\nRequestId: {3}", exc.Response.StatusCode, (int)exc.Response.StatusCode, exc.Response.Content, exc.Response.Headers["RequestId"].FirstOrDefault());
                return false;
            }

            return true;
        }

        public async Task<bool> EmbedDashboard()
        {
            // Get token credentials for user
            var getCredentialsResult = await GetTokenCredentials();
            if (!getCredentialsResult)
            {
                // The error message set in GetTokenCredentials
                return false;
            }

            try
            {
                // Create a Power BI Client object. It will be used to call Power BI APIs.
                using (var client = new PowerBIClient(new Uri(ApiUrl), m_tokenCredentials))
                {
                    // Get a list of dashboards.
                    var dashboards = await client.Dashboards.GetDashboardsInGroupAsync(new Guid(WorkspaceId));

                    // Get the first report in the workspace.
                    var dashboard = dashboards.Value.FirstOrDefault();

                    if (dashboard == null)
                    {
                        m_embedConfig.ErrorMessage = "Workspace has no dashboards.";
                        return false;
                    }

                    // Generate Embed Token.
                    var generateTokenRequestParameters = new GenerateTokenRequest(accessLevel: "view");
                    var tokenResponse = await client.Dashboards.GenerateTokenInGroupAsync(new Guid(WorkspaceId), dashboard.Id, generateTokenRequestParameters);

                    if (tokenResponse == null)
                    {
                        m_embedConfig.ErrorMessage = "Failed to generate embed token.";
                        return false;
                    }

                    // Generate Embed Configuration.
                    m_embedConfig = new EmbedConfig()
                    {
                        EmbedToken = tokenResponse,
                        EmbedUrl = dashboard.EmbedUrl,
                        Id = dashboard.Id.ToString()
                    };

                    return true;
                }
            }
            catch (HttpOperationException exc)
            {
                m_embedConfig.ErrorMessage = string.Format("Status: {0} ({1})\r\nResponse: {2}\r\nRequestId: {3}", exc.Response.StatusCode, (int)exc.Response.StatusCode, exc.Response.Content, exc.Response.Headers["RequestId"].FirstOrDefault());
                return false;
            }
        }

        public async Task<bool> EmbedTile()
        {
            // Get token credentials for user
            var getCredentialsResult = await GetTokenCredentials();
            if (!getCredentialsResult)
            {
                // The error message set in GetTokenCredentials
                m_tileEmbedConfig.ErrorMessage = m_embedConfig.ErrorMessage;
                return false;
            }

            try
            {
                // Create a Power BI Client object. It will be used to call Power BI APIs.
                using (var client = new PowerBIClient(new Uri(ApiUrl), m_tokenCredentials))
                {
                    // Get a list of dashboards.
                    var dashboards = await client.Dashboards.GetDashboardsInGroupAsync(new Guid(WorkspaceId));

                    // Get the first report in the workspace.
                    var dashboard = dashboards.Value.FirstOrDefault();

                    if (dashboard == null)
                    {
                        m_tileEmbedConfig.ErrorMessage = "Workspace has no dashboards.";
                        return false;
                    }

                    var tiles = await client.Dashboards.GetTilesInGroupAsync(new Guid(WorkspaceId), dashboard.Id);

                    // Get the first tile in the workspace.
                    var tile = tiles.Value.FirstOrDefault();

                    // Generate Embed Token for a tile.
                    var generateTokenRequestParameters = new GenerateTokenRequest(accessLevel: "view");
                    var tokenResponse = await client.Tiles.GenerateTokenInGroupAsync(new Guid(WorkspaceId), dashboard.Id, tile.Id, generateTokenRequestParameters);

                    if (tokenResponse == null)
                    {
                        m_tileEmbedConfig.ErrorMessage = "Failed to generate embed token.";
                        return false;
                    }

                    // Generate Embed Configuration.
                    m_tileEmbedConfig = new TileEmbedConfig()
                    {
                        EmbedToken = tokenResponse,
                        EmbedUrl = tile.EmbedUrl,
                        Id = tile.Id.ToString(),
                        dashboardId = dashboard.Id.ToString()
                    };

                    return true;
                }
            }
            catch (HttpOperationException exc)
            {
                m_embedConfig.ErrorMessage = string.Format("Status: {0} ({1})\r\nResponse: {2}\r\nRequestId: {3}", exc.Response.StatusCode, (int)exc.Response.StatusCode, exc.Response.Content, exc.Response.Headers["RequestId"].FirstOrDefault());
                return false;
            }
        }

        /// <summary>
        /// Check if web.config embed parameters have valid values.
        /// </summary>
        /// <returns>Null if web.config parameters are valid, otherwise returns specific error string.</returns>
        private string GetWebConfigErrors()
        {
            // Application Id must have a value.
            if (string.IsNullOrWhiteSpace(ApplicationId))
            {
                return "ApplicationId is empty. please register your application as Native app in https://dev.powerbi.com/apps and fill client Id in web.config.";
            }

            // Application Id must be a Guid object.
            Guid result;
            if (!Guid.TryParse(ApplicationId, out result))
            {
                return "ApplicationId must be a Guid object. please register your application as Native app in https://dev.powerbi.com/apps and fill application Id in web.config.";
            }

            // Workspace Id must have a value.
            if (string.IsNullOrWhiteSpace(WorkspaceId))
            {
                return "WorkspaceId is empty. Please select a group you own and fill its Id in web.config";
            }

            // Workspace Id must be a Guid object.
            if (!Guid.TryParse(WorkspaceId, out result))
            {
                return "WorkspaceId must be a Guid object. Please select a workspace you own and fill its Id in web.config";
            }

            if (AuthenticationType.Equals("MasterUser"))
            {
                // Username must have a value.
                if (string.IsNullOrWhiteSpace(Username))
                {
                    return "Username is empty. Please fill Power BI username in web.config";
                }

                // Password must have a value.
                if (string.IsNullOrWhiteSpace(Password))
                {
                    return "Password is empty. Please fill password of Power BI username in web.config";
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(ApplicationSecret))
                {
                    return "ApplicationSecret is empty. please register your application as Web app and fill appSecret in web.config.";
                }

                // Must fill tenant Id
                if (string.IsNullOrWhiteSpace(Tenant))
                {
                    return "Invalid Tenant. Please fill Tenant ID in Tenant under web.config";
                }
            }

            return null;
        }

        private async Task<AuthenticationResult> DoAuthentication()
        {
            AuthenticationResult authenticationResult = null;
            if (AuthenticationType.Equals("MasterUser"))
            {
                var authenticationContext = new AuthenticationContext(AuthorityUrl);

                // Authentication using master user credentials
                var credential = new UserPasswordCredential(Username, Password);
                authenticationResult = authenticationContext.AcquireTokenAsync(ResourceUrl, ApplicationId, credential).Result;
            }
            else
            {
                // For app only authentication, we need the specific tenant id in the authority url
                var tenantSpecificURL = AuthorityUrl.Replace("common", Tenant);
                var authenticationContext = new AuthenticationContext(tenantSpecificURL);

                // Authentication using app credentials
                var credential = new ClientCredential(ApplicationId, ApplicationSecret);
                authenticationResult = await authenticationContext.AcquireTokenAsync(ResourceUrl, credential);
            }

            return authenticationResult;
        }

        private async Task<bool> GetTokenCredentials()
        {
            // var result = new EmbedConfig { Username = username, Roles = roles };
            var error = GetWebConfigErrors();
            if (error != null)
            {
                m_embedConfig.ErrorMessage = error;
                return false;
            }

            // Authenticate using created credentials
            AuthenticationResult authenticationResult = null;
            try
            {
                authenticationResult = await DoAuthentication();
            }
            catch (AggregateException exc)
            {
                m_embedConfig.ErrorMessage = exc.InnerException.Message;
                return false;
            }

            if (authenticationResult == null)
            {
                m_embedConfig.ErrorMessage = "Authentication Failed.";
                return false;
            }

            m_tokenCredentials = new TokenCredentials(authenticationResult.AccessToken, "Bearer");
            return true;
        }
    }
}