using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.SourceBrowser.SourceIndexServer.Models;

namespace Microsoft.SourceBrowser.SourceIndexServer.HealthChecks
{
    /// <summary>
    /// Health check for Azure Blob Storage connectivity.
    /// Verifies that the storage URL is configured and accessible.
    /// </summary>
    public class StorageHealthCheck : IHealthCheck
    {
        private readonly ILogger<StorageHealthCheck> _logger;

        public StorageHealthCheck(ILogger<StorageHealthCheck> logger)
        {
            _logger = logger;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var storageUrl = Helpers.IndexProxyUrl;

                if (string.IsNullOrEmpty(storageUrl))
                {
                    _logger.LogWarning("Storage health check failed: SOURCE_BROWSER_INDEX_PROXY_URL not configured");
                    return Task.FromResult(
                        HealthCheckResult.Unhealthy(
                            "Storage URL not configured",
                            data: new Dictionary<string, object>
                            {
                                ["config_key"] = "SOURCE_BROWSER_INDEX_PROXY_URL"
                            }));
                }

                // Check storage access by verifying a marker file exists
                var fs = new AzureBlobFileSystem(storageUrl);
                var testFile = "/.health";
                var exists = fs.FileExists(testFile);

                _logger.LogInformation(
                    "Storage health check passed: Storage accessible, test_file={TestFile}, exists={Exists}",
                    testFile, exists);

                if (!exists)
                {
                    return Task.FromResult(
                        HealthCheckResult.Unhealthy(
                            "Storage could not be verified",
                            data: new Dictionary<string, object>
                            {
                                ["error_type"] = "HealthMarkerMissing"
                            }));
                }

                return Task.FromResult(
                    HealthCheckResult.Healthy(
                        "Storage accessible",
                        data: new Dictionary<string, object>
                        {
                            ["storage_url"] = storageUrl,
                            ["test_file"] = testFile,
                            ["file_exists"] = exists
                        }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage health check failed: Storage access error");
                return Task.FromResult(
                    HealthCheckResult.Unhealthy(
                        "Storage access failed",
                        exception: ex,
                        data: new Dictionary<string, object>
                        {
                            ["error_type"] = ex.GetType().Name
                        }));
            }
        }
    }
}
