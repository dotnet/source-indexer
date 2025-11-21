using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SourceBrowser.SourceIndexServer.Models;

namespace Microsoft.SourceBrowser.SourceIndexServer
{
    public class Startup
    {
        public Startup(IWebHostEnvironment env)
        {
            Environment = env;
        }

        public IWebHostEnvironment Environment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            RootPath = Path.Combine(Environment.ContentRootPath, "index");

            var subfolder = Path.Combine(RootPath, "index");
            if (File.Exists(Path.Combine(subfolder, "Projects.txt")))
            {
                RootPath = subfolder;
            }

            services.AddSingleton(new Index(RootPath));
            services.AddControllersWithViews();
            services.AddRazorPages();

            // Add health checks
            services.AddHealthChecks()
                .AddCheck<HealthChecks.StorageHealthCheck>(
                    name: "storage",
                    tags: ["ready"])
                .AddCheck(
                    name: "startup",
                    check: () => HealthCheckResult.Healthy("Application is running"),
                    tags: ["alive"]);
        }

        public string RootPath { get; set; }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // Configure forwarded headers for Azure Front Door
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                  ForwardedHeaders.XForwardedProto |
                                  ForwardedHeaders.XForwardedHost,
                KnownNetworks = { },
                KnownProxies = { }
            });

            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-UA-Compatible"] = "IE=edge";
                await next();
            });

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.Use(Helpers.ServeProxiedIndex);

            app.UseDefaultFiles();
            if (Directory.Exists(RootPath))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(RootPath, ExclusionFilters.Sensitive & ~ExclusionFilters.DotPrefixed),
                });
            }
            app.UseStaticFiles();
            app.UseRouting();

            app.UseEndpoints(endPoints =>
            {
                const int healthCacheSeconds = 30;

                static Task CacheableMinimalResponse(HttpContext context, HealthReport report)
                {
                    context.Response.Headers.CacheControl = $"public,max-age={healthCacheSeconds}";
                    context.Response.Headers.Pragma = "public";
                    context.Response.Headers.Expires = "0";
                    return HealthChecks.HealthCheckResponseWriter.WriteMinimalResponse(context, report);
                }

                // Health check endpoints
                // Basic health check with minimal information (cached by default)
                endPoints.MapHealthChecks("/health", new HealthCheckOptions
                {
                    Predicate = _ => true,
                    ResponseWriter = CacheableMinimalResponse
                });

                // Liveness probe (always healthy if app is running)
                endPoints.MapHealthChecks("/health/alive", new HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("alive"),
                    ResponseWriter = HealthChecks.HealthCheckResponseWriter.WriteMinimalResponse
                });

                if (env.IsDevelopment() || Helpers.DebugLoggingEnabled)
                {
                    // Detailed health check with full diagnostics
                    endPoints.MapHealthChecks("/health/detailed", new HealthCheckOptions
                    {
                        Predicate = _ => true,
                        ResponseWriter = HealthChecks.HealthCheckResponseWriter.WriteResponse
                    });

                    // Readiness probe (checks storage)
                    endPoints.MapHealthChecks("/health/ready", new HealthCheckOptions
                    {
                        Predicate = check => check.Tags.Contains("ready"),
                        ResponseWriter = HealthChecks.HealthCheckResponseWriter.WriteMinimalResponse
                    });
                }

                endPoints.MapRazorPages();
                endPoints.MapControllers();
            });

            // Retrieve and store the logger
            Program.Logger = app.ApplicationServices.GetService<ILogger<Program>>();
        }
    }
}
