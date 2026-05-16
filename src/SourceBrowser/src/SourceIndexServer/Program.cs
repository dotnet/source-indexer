using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
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
        public static ILogger Logger { get; set; }

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.AddServiceDefaults();

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddAzureWebAppDiagnostics();

            builder.Host.UseWindowsService();

            var rootPath = Path.Combine(builder.Environment.ContentRootPath, "index");
            var subfolder = Path.Combine(rootPath, "index");
            if (File.Exists(Path.Combine(subfolder, "Projects.txt")))
            {
                rootPath = subfolder;
            }

            builder.Services.AddSingleton(new Index(rootPath));
            builder.Services.AddControllersWithViews();
            builder.Services.AddRazorPages();

            var app = builder.Build();

            app.MapDefaultEndpoints();

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

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.Use(Helpers.ServeProxiedIndex);

            app.UseDefaultFiles();
            if (Directory.Exists(rootPath))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(rootPath, ExclusionFilters.Sensitive & ~ExclusionFilters.DotPrefixed),
                });
            }
            app.UseStaticFiles();
            app.UseRouting();

            app.MapRazorPages();
            app.MapControllers();

            Logger = app.Services.GetService<ILogger<Program>>();

            app.Run();
        }
    }
}
