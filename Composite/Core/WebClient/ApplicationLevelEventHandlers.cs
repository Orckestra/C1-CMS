using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Web;
using Composite.AspNet;
using Composite.C1Console.Actions.Data;
using Composite.C1Console.Elements;
using Composite.C1Console.Events;
using Composite.Search;
using Composite.Core.Application;
using Composite.Core.Configuration;
using Composite.Core.Extensions;
using Composite.Core.Implementation;
using Composite.Core.Instrumentation;
using Composite.Core.Logging;
using Composite.Core.Routing;
using Composite.Core.Threading;
using Composite.Core.Types;
using Composite.Data.Types;
using Composite.Functions;
using Composite.Plugins.Elements.UrlToEntityToken;
using Composite.Plugins.Routing.InternalUrlConverters;
using Microsoft.Extensions.DependencyInjection;

namespace Composite.Core.WebClient
{
    /// <summary>    
    /// ASP.NET Application level logic. This class primarily interact between C1 CMS and the ASP.NET Application.  
    /// Most of the members on this class is not documented, except for those which developers may find useful to interact with.
    /// </summary>
    public static class ApplicationLevelEventHandlers
    {
        private const string _verboseLogEntryTitle = "RGB(205, 92, 92)ApplicationEventHandler";
        static readonly object _syncRoot = new object();
        private static DateTime _startTime;
        private static bool _systemIsInitialized;
        private static readonly ConcurrentDictionary<string, Func<HttpContext, string>> _c1PageCustomStringProviders = new ConcurrentDictionary<string, Func<HttpContext, string>>();

        /// <exclude />
        public static bool LogRequestDetails { get; set; }

        /// <exclude />
        public static bool LogApplicationLevelErrors { get; set; }


        

        /// <exclude />
        public static void Application_Start(object sender, EventArgs e)
        {
            _startTime = DateTime.Now;
            if (RuntimeInformation.IsDebugBuild)
            {
                Log.LogInformation(_verboseLogEntryTitle, "AppDomain {0} started at {1} in process {2}", AppDomain.CurrentDomain.Id, _startTime.ToString("HH:mm:ss:ff"), Process.GetCurrentProcess().Id);
            }

            SystemSetupFacade.SetFirstTimeStart();

            InitializeServices();

            if (!SystemSetupFacade.IsSystemFirstTimeInitialized)
            {
                return;
            }

            if (_systemIsInitialized)
            {
                return;
            }

            if (AppDomain.CurrentDomain.BaseDirectory.Length > GlobalSettingsFacade.MaximumRootPathLength)
            {
                throw new InvalidOperationException("Windows limitation problem detected! You have installed the website at a place where the total path length of the file with the longest filename exceeds the maximum allowed in Windows. See http://msdn.microsoft.com/en-us/library/aa365247%28VS.85%29.aspx#paths");
            }

            
            AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;

            lock (_syncRoot)
            {
                if (_systemIsInitialized) return;

                ApplicationStartInitialize(RuntimeInformation.IsDebugBuild);

                _systemIsInitialized = true;
            }
        }


        private static void InitializeServices()
        {
            UrlToEntityTokenFacade.Register(new DataUrlToEntityTokenMapper());
            UrlToEntityTokenFacade.Register(new MediaUrlToEntityTokenMapper());
            UrlToEntityTokenFacade.Register(new ServerLogUrlToEntityTokenMapper());
            UrlToEntityTokenFacade.Register(new WebsiteFileUrlToEntityTokenMapper());

            var services = ServiceLocator.ServiceCollection;
            services.AddLogging();
            services.AddRoutedData();
            services.AddDataActionTokenResolver();
            services.AddDefaultSearchDocumentSourceProviders();

            InternalUrls.Register(new MediaInternalUrlConverter());
            InternalUrls.Register(new PageInternalUrlConverter());

            services.AddSingleton<IMailer>(new SmtpMailer());

            services.AddTransient<ISiteMapPlugin, CmsPagesSiteMapPlugin>();

            VersionedDataHelper.Initialize();
        }


        /// <exclude />
        public static void Application_End(object sender, EventArgs e)
        {
            if (RuntimeInformation.IsDebugBuild)
            {
                Log.LogInformation(_verboseLogEntryTitle, "AppDomain {0} ended at {1} in process {2}", AppDomain.CurrentDomain.Id, DateTime.Now.ToString("HH:mm:ss:ff"), Process.GetCurrentProcess().Id);
            }

            if (!SystemSetupFacade.IsSystemFirstTimeInitialized)
            {
                return;
            }


            using (ThreadDataManager.Initialize())
            {
                try
                {
                    CodeGenerationManager.ValidateCompositeGenerate(_startTime);
                    CodeGenerationManager.GenerateCompositeGeneratedAssembly();
                }
                catch (Exception ex)
                {
                    Log.LogCritical("Global.asax", "Error updating Composite.Generated.dll");
                    Log.LogCritical("Global.asax", ex);
                }               

                try
                {
                    GlobalEventSystemFacade.PrepareForShutDown();
                    if (RuntimeInformation.IsDebugBuild)
                    {
                        LogShutDownReason();
                    }
                    GlobalEventSystemFacade.ShutDownTheSystem();
                    
                    TempDirectoryFacade.OnApplicationEnd();
                }
                catch (Exception ex)
                {
                    Log.LogCritical("Global.asax", ex);

                    throw;
                }

                Log.LogVerbose("Global.asax", $"--- Web Application End, {DateTime.Now.ToLongTimeString()} Id = {AppDomain.CurrentDomain.Id}---");
            }
        }


        /// <exclude />
        public static void Application_BeginRequest(object sender, EventArgs e)
        {
            var context = ((HttpApplication) sender).Context;

            ThreadDataManager.InitializeThroughHttpContext();

            if (SystemSetupFacade.IsSystemFirstTimeInitialized)
            {
            ServiceLocator.CreateRequestServicesScope(context);
            }

            if (LogRequestDetails)
            {
                // LoggingService.LogVerbose("Begin request", string.Format("{0}", Request.Path));
                context.Items.Add("Global.asax timer", Environment.TickCount);
            }
        }


        /// <exclude />
        public static void Application_EndRequest(object sender, EventArgs e)
        {
            var context = ((HttpApplication) sender).Context;

            try
            {
                ServiceLocator.DisposeRequestServicesScope(context);

                if (LogRequestDetails && context.Items.Contains("Global.asax timer"))
                {
                    int startTimer = (int)context.Items["Global.asax timer"];
                    string requestPath = context.Request.Path;
                    Log.LogVerbose("End request", $"{requestPath} - took {Environment.TickCount - startTimer} ms");
                }
            }
            finally
            {
                ThreadDataManager.FinalizeThroughHttpContext();
            }
        }



        /// <exclude />
        public static void Application_Error(object sender, EventArgs e)
        {
            var httpApplication = (HttpApplication) sender;
            Exception exception = httpApplication.Server.GetLastError();

            var eventType = TraceEventType.Error;

            var httpContext = httpApplication.Context;

            if (httpContext != null)
            {
                bool is404 = exception is HttpException httpException
                             && httpException.GetHttpCode() == 404;

                if (is404)
                {
                    string rawUrl = httpContext.Request.RawUrl;

                    if (!UrlUtils.IsAdminConsoleRequest(rawUrl))
                    {
                        string customPageNotFoundUrl = HostnameBindingsFacade.GetCustomPageNotFoundUrl();

                        if (!customPageNotFoundUrl.IsNullOrEmpty())
                        {
                            if (rawUrl == customPageNotFoundUrl)
                            {
                                throw new HttpException(500, $"'Page not found' url isn't handled. Url: '{rawUrl}'");
                            }

                            httpContext.Server.ClearError();
                            httpContext.Response.Clear();

                            httpContext.Response.Redirect(customPageNotFoundUrl, true);

                            return;
                        }

                        eventType = TraceEventType.Verbose;
                    }
                }

                // Logging request url
                if (LogApplicationLevelErrors)
                {
                    HttpRequest request = null;

                    try
                    {
                        request = httpContext.Request;
                    }
                    catch
                    {
                        // Request may not be available at this point
                    }

                    if (request != null)
                    {
                        LoggingService.LogEntry("Application Error",
                                            $"Failed to process '{request.RequestType}' request to url '{request.RawUrl}'",
                                            LoggingService.Category.General, eventType);
                    }
                }
            }

            if (LogApplicationLevelErrors)
            {
                while (exception != null)
                {
                    LoggingService.LogEntry("Application Error", exception.ToString(), LoggingService.Category.General, eventType);
                    exception = exception.InnerException;
                }
            }
        }


        /// <exclude />
        public static string GetVaryByCustomString(HttpContext context, string custom)
        {
            if (custom == "C1Page")
            {
                string rawUrl = context.Request.RawUrl;

                UrlKind urlKind;
                var pageUrl = PageUrls.UrlProvider.ParseUrl(rawUrl, new UrlSpace(context), out urlKind);

                var page = pageUrl?.GetPage();
                if (page != null)
                {
                    var pageCacheKey = new StringBuilder(page.ChangeDate.ToString(CultureInfo.InvariantCulture));

                    if (context.Request.IsSecureConnection)
                    {
                        pageCacheKey.Append("https");
                    }

                    // Adding the relative path from RawUrl as a part of cache key to make ASP.NET cache respect casing of urls
                    pageCacheKey.Append(new UrlBuilder(rawUrl).FullPath);
                    foreach (string key in _c1PageCustomStringProviders.Keys)
                    {
                        pageCacheKey.Append(_c1PageCustomStringProviders[key](context));
                    }

                    return pageCacheKey.ToString();
                }
                return string.Empty;
            }

            return null;
        }


        /// <summary>
        /// Register a function that provide a custom string to be part of a C1 Page cache key. You should register a function just once and during the application initialization.
        /// Your function will be called with the current HttpContext and should return either null or a string for the C1 Page request in relation to caching.
        /// An example situation where this can be used: You want to have full page caching, but you have C1 Page content being dependant on client settings, such as HTTP CLIENT.
        /// You should kep the 'spread in values' you return to a minimum - each unique string will create a new cache entry and consume memory.
        /// </summary>
        /// <param name="providerId">A string unique for your function - used to ensure this is only registered once</param>
        /// <param name="customStringBuilder">Your function the can return a custom string</param>
        public static void RegisterC1PageVaryByCustomStringProvider( string providerId, Func<HttpContext,string> customStringBuilder)
        {
            _c1PageCustomStringProviders.GetOrAdd(providerId, customStringBuilder);
        }


        /// <exclude />
        public static void ApplicationStartInitialize(bool displayDebugInfo = false)
        {
            ThreadDataManager.InitializeThroughHttpContext();

            if (displayDebugInfo)
            {
                Log.LogVerbose("Global.asax", "--- Web Application Start, {0} Id = {1} ---", DateTime.Now.ToLongTimeString(), AppDomain.CurrentDomain.Id);
            }

            PerformanceCounterFacade.SystemStartupIncrement();

            using (GlobalInitializerFacade.GetPreInitHandlersScope())
            {
                ApplicationStartupFacade.FireConfigureServices(ServiceLocator.ServiceCollection);

                ServiceLocator.BuildServiceProvider();
                ServiceLocator.CreateRequestServicesScope(HttpContext.Current);
                HttpRuntime.WebObjectActivator = new WebObjectActivator(ServiceLocator.ServiceProvider);

                ApplicationStartupFacade.FireBeforeSystemInitialize(ServiceLocator.ServiceProvider);
            }

            TempDirectoryFacade.OnApplicationStart();

            HostnameBindingsFacade.Initialize();

            ApplicationStartupFacade.FireSystemInitialized(ServiceLocator.ServiceProvider);

            ThreadDataManager.FinalizeThroughHttpContext();
        }



        private static void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            if (RuntimeInformation.IsDebugBuild)
            {
                Log.LogInformation(_verboseLogEntryTitle, $"AppDomain {AppDomain.CurrentDomain.Id} unloaded at {DateTime.Now:HH:mm:ss:ff}");
            }
        }



        private static void LogShutDownReason()
        {
            HttpRuntime runtime = (HttpRuntime)typeof(System.Web.HttpRuntime).InvokeMember("_theRuntime",
                                                                                        System.Reflection.BindingFlags.NonPublic |
                                                                                        System.Reflection.BindingFlags.Static |
                                                                                        System.Reflection.BindingFlags.GetField,
                                                                                        null, null, null);

            if (runtime == null)
            {
                Log.LogWarning("ASP.NET Shut Down", "Unable to determine cause of shut down");
                return;
            }

            string shutDownMessage = (string)runtime.GetType().InvokeMember("_shutDownMessage",
                                                                         System.Reflection.BindingFlags.NonPublic |
                                                                         System.Reflection.BindingFlags.Instance |
                                                                         System.Reflection.BindingFlags.GetField,
                                                                         null, runtime, null);

            string shutDownStack = (string)runtime.GetType().InvokeMember("_shutDownStack",
                                                                       System.Reflection.BindingFlags.NonPublic |
                                                                       System.Reflection.BindingFlags.Instance |
                                                                       System.Reflection.BindingFlags.GetField,
                                                                       null, runtime, null);

            shutDownMessage = (shutDownMessage ?? "null").Replace("\n", "   \n");

            Log.LogVerbose("RGB(250,50,50)ASP.NET Shut Down",
                $"_shutDownMessage=\n{shutDownMessage}\n\n_shutDownStack=\n{shutDownStack}");

        }
    }
}
