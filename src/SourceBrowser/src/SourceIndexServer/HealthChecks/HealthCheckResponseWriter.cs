using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.SourceBrowser.SourceIndexServer.HealthChecks
{
    /// <summary>
    /// Custom response writer for health check endpoints.
    /// Provides detailed JSON output for diagnostics.
    /// </summary>
    public static class HealthCheckResponseWriter
    {
        public static Task WriteResponse(HttpContext context, HealthReport healthReport)
        {
            context.Response.ContentType = "application/json; charset=utf-8";

            var options = new JsonWriterOptions { Indented = true };

            using var memoryStream = new MemoryStream();
            using (var jsonWriter = new Utf8JsonWriter(memoryStream, options))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("status", healthReport.Status.ToString());
                jsonWriter.WriteString("timestamp", DateTime.UtcNow);
                jsonWriter.WriteNumber("total_duration_ms", healthReport.TotalDuration.TotalMilliseconds);

                jsonWriter.WriteStartObject("checks");

                foreach (var healthReportEntry in healthReport.Entries)
                {
                    jsonWriter.WriteStartObject(healthReportEntry.Key);
                    jsonWriter.WriteString("status", healthReportEntry.Value.Status.ToString());
                    jsonWriter.WriteString("description", healthReportEntry.Value.Description);
                    jsonWriter.WriteNumber("duration_ms", healthReportEntry.Value.Duration.TotalMilliseconds);

                    if (healthReportEntry.Value.Exception != null)
                    {
                        var ex = healthReportEntry.Value.Exception;
                        jsonWriter.WriteString("exception", ex.Message);
                        jsonWriter.WriteString("exception_type", ex.GetType().FullName);
                        jsonWriter.WriteString("stack_trace", ex.StackTrace);

                        // Include inner exception details if present
                        if (ex.InnerException != null)
                        {
                            jsonWriter.WriteStartObject("inner_exception");
                            jsonWriter.WriteString("message", ex.InnerException.Message);
                            jsonWriter.WriteString("type", ex.InnerException.GetType().FullName);
                            jsonWriter.WriteString("stack_trace", ex.InnerException.StackTrace);
                            jsonWriter.WriteEndObject();
                        }
                    }

                    jsonWriter.WriteStartObject("data");

                    foreach (var item in healthReportEntry.Value.Data)
                    {
                        jsonWriter.WritePropertyName(item.Key);

                        JsonSerializer.Serialize(jsonWriter, item.Value,
                            item.Value?.GetType() ?? typeof(object));
                    }

                    jsonWriter.WriteEndObject();
                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndObject();
            }

            return context.Response.WriteAsync(
                Encoding.UTF8.GetString(memoryStream.ToArray()));
        }

        public static Task WriteMinimalResponse(HttpContext context, HealthReport healthReport)
        {
            context.Response.ContentType = "application/json; charset=utf-8";

            var options = new JsonWriterOptions { Indented = false };

            using var memoryStream = new MemoryStream();
            using (var jsonWriter = new Utf8JsonWriter(memoryStream, options))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("status", healthReport.Status.ToString());
                jsonWriter.WriteString("timestamp", DateTime.UtcNow);
                jsonWriter.WriteEndObject();
            }

            return context.Response.WriteAsync(
                Encoding.UTF8.GetString(memoryStream.ToArray()));
        }
    }
}
