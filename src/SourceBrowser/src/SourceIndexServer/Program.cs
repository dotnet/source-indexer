using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SourceBrowser.SourceIndexServer.Models;

namespace Microsoft.SourceBrowser.SourceIndexServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add Aspire service defaults
            builder.AddServiceDefaults();

            // Configure logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddAzureWebAppDiagnostics();

            // Add Windows Service support
            builder.Host.UseWindowsService();

            // Configure services
            ConfigureServices(builder);

            var app = builder.Build();

            // Set the service provider for AzureBlobFileSystem when running under Aspire orchestration
            if (Environment.GetEnvironmentVariable("SOURCE_BROWSER_ASPIRE_ORCHESTRATED") == "true")
            {
                AzureBlobFileSystem.ServiceProvider = app.Services;
            }

            // Configure the HTTP request pipeline
            Configure(app);

            // Store logger for global access
            Logger = app.Services.GetRequiredService<ILogger<Program>>();

            // Add Aspire health checks and endpoints
            app.MapDefaultEndpoints();

            app.Run();
        }

        public static ILogger Logger { get; set; }

        private static void ConfigureServices(WebApplicationBuilder builder)
        {
            // Add Aspire Azure Storage Blobs service client
            builder.AddAzureBlobServiceClient("sourceindex-blobs");

            // Configure the root path for source index
            var rootPath = Path.Combine(builder.Environment.ContentRootPath, "index");
            var subfolder = Path.Combine(rootPath, "index");
            if (File.Exists(Path.Combine(subfolder, "Projects.txt")))
            {
                rootPath = subfolder;
            }

            builder.Services.AddSingleton(new Models.Index(rootPath));
            builder.Services.AddControllersWithViews();
            builder.Services.AddRazorPages();
        }

        private static void Configure(WebApplication app)
        {
            // Add custom header middleware
            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-UA-Compatible"] = "IE=edge";
                await next();
            });

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.Use(Helpers.ServeProxiedIndex);

            app.UseDefaultFiles();
            
            // Configure static files for the index using the static RootPath
            if (Directory.Exists(Models.Index.RootPath))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(Models.Index.RootPath, ExclusionFilters.Sensitive & ~ExclusionFilters.DotPrefixed),
                });
            }
            
            app.UseStaticFiles();
            app.UseRouting();

            app.MapRazorPages();
            app.MapControllers();
        }
    }
}
