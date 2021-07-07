using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using System.Xml.Linq;
using Composite.C1Console.Forms.CoreUiControls;
using Composite.Core.ResourceSystem;
using Composite.Core.ResourceSystem.Icons;


namespace Composite.Core.IO
{
    /// <summary>
    /// </summary>
    /// <exclude />
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)] 
	public static class MimeTypeInfo
    {
        private static readonly string LogTitle = typeof (MimeTypeInfo).Name;

        private static readonly IDictionary<string, string> _toCanonical = new Dictionary<string, string>();
        private static readonly IDictionary<string, string> _extensionToCanonical = new Dictionary<string, string>();
        private static readonly IDictionary<string, string> _mimeTypeToResourceName = new Dictionary<string, string>();
        private static readonly IDictionary<string, string> _mimeTypeToExtension = new Dictionary<string, string>();
        private static readonly ConcurrentDictionary<string, bool> _iisServeableExtensions = new ConcurrentDictionary<string, bool>();

        private static List<string> _textMimeTypes =
            new List<string> { MimeTypeInfo.Css, MimeTypeInfo.Js, MimeTypeInfo.Json, MimeTypeInfo.Xml, MimeTypeInfo.Text, MimeTypeInfo.Html, MimeTypeInfo.Sass,
                               MimeTypeInfo.Ascx, MimeTypeInfo.Ashx, MimeTypeInfo.Asmx, MimeTypeInfo.Aspx, MimeTypeInfo.Asax, MimeTypeInfo.CSharp, 
                               MimeTypeInfo.Resx, MimeTypeInfo.MasterPage, MimeTypeInfo.CsHtml, MimeTypeInfo.Svg };

        // file types we don't expect IIS to block
        private static readonly HashSet<string> _iisServeableTypes = new HashSet<string>();

        private static ResourceHandle GetIconHandle(string name)
        {
            return new ResourceHandle(BuildInIconProviderName.ProviderName, name);
        }

        /// <exclude />
        public static string Default => "application/octet-stream";

        /// <exclude />
        public static string Jpeg => "image/jpeg";

        /// <exclude />
        public static string Gif => "image/gif";

        /// <exclude />
        public static string Bmp => "image/bmp";

        /// <exclude />
        public static string Png => "image/png";

        /// <exclude />
        public static string Tiff => "image/tiff";

        /// <exclude />
        public static string WebP => "image/webp";

        /// <exclude />
        public static string Css => "text/css";

        /// <exclude />
        public static string Sass => "text/x-sass";

        /// <exclude />
        public static string Js => "text/js";

        /// <exclude />
        public static string Json => "application/json";

        /// <exclude />
        public static string Xml => "text/xml";

        /// <exclude />
        public static string Text => "text/plain";

        /// <exclude />
        public static string Html => "text/html";

        /// <exclude />
        public static string Flash => "application/x-shockwave-flash";

        /// <exclude />
        public static string QuickTime => "video/quicktime";

        /// <exclude />
        public static string Pdf = "application/pdf";

        /// <exclude />
        public static string Wmv => "video/x-ms-wmv";

        /// <exclude />
        public static string Asf => "video/x-ms-asf";

        /// <exclude />
        public static string Avi => "video/x-msvideo";

        /// <exclude />
        public static string Flv => "video/x-flv";

        /// <exclude />
        public static string Director => "application/x-director";

        /// <exclude />
        public static string CSharp => "text/x-csharp";

        /// <exclude />
        public static string CsHtml => "application/x-cshtml";

        /// <exclude />
        public static string Svg => "image/svg+xml";

        /// <exclude />
        public static string Ascx => "application/x-ascx";

        /// <exclude />
        public static string Aspx => "application/x-aspx";

        /// <exclude />
        public static string Asax => "application/x-asax";

        /// <exclude />
        public static string Ashx => "application/x-ashx";

        /// <exclude />
        public static string Asmx => "application/x-asmx";

        /// <exclude />
        public static string Resx => "application/x-resx";

        /// <exclude />
        public static string MasterPage => "application/x-master-page";


        /// <exclude />
        static MimeTypeInfo()
        {
            LoadExtensionMappingsFromWebConfig();

            // Image formats
            _toCanonical.Add("image/pjpg", Jpeg);
            _toCanonical.Add("image/pjpeg", Jpeg);
            _toCanonical.Add("image/jpg", Jpeg);
            RegisterMimeType(MimeTypeInfo.Jpeg, new [] {"jpg", "jpe", "jpeg"}, "mimetype-jpeg", true);

            RegisterMimeType(MimeTypeInfo.Gif, "gif", "mimetype-gif", true);
            RegisterMimeType(MimeTypeInfo.Bmp, "bmp", "mimetype-bmp", true);

            _toCanonical.Add("image/x-png", Png);
            RegisterMimeType(MimeTypeInfo.Png, "png", "mimetype-png", true);

            RegisterMimeType(MimeTypeInfo.WebP, "webp", "mimetype-webp", true);

            RegisterMimeType(MimeTypeInfo.Svg, "svg", "mimetype-svg", true);

            _toCanonical.Add("image/tif", MimeTypeInfo.Tiff);
            RegisterMimeType(MimeTypeInfo.Tiff, "tif", "mimetype-tiff", true);

            // Web
            RegisterMimeType(MimeTypeInfo.Css, new[] { "css", "less" }, "mimetype-css", true);
            RegisterMimeType(MimeTypeInfo.Sass, new[] { "scss" }, "mimetype-css", true);
            RegisterMimeType(MimeTypeInfo.Resx, "resx", "mimetype-resx");

            _toCanonical.Add("application/x-javascript", MimeTypeInfo.Js);
            RegisterMimeType(MimeTypeInfo.Js, "js", "mimetype-js", true);

            RegisterMimeType(MimeTypeInfo.Json, new[] { "json" }, "mimetype-js");
            RegisterMimeType(MimeTypeInfo.Html, new[] { "htm", "html", "xhtml" }, "mimetype-html", true);

            // Audio/Video
            RegisterMimeType("audio/x-wav", "wav", null, true);
            RegisterMimeType("audio/x-pn-realaudio", new[] { "ram", "rm" }, "mimetype-ram", true);
            RegisterMimeType("audio/mpeg", "mp3", "mimetype-mp3", true);
            RegisterMimeType("video/mpeg", new[] { "mpeg", "mpg" }, "mimetype-mpeg", true);
            RegisterMimeType(MimeTypeInfo.Flv, "flv", null, true);
            RegisterMimeType(MimeTypeInfo.Asf, "asf", "mimetype-asf", true);
            RegisterMimeType(MimeTypeInfo.Avi, "avi", "mimetype-movie", true);
            RegisterMimeType(MimeTypeInfo.Wmv, "wmv", "mimetype-wmv", true);

            // Applications
            RegisterMimeType("application/postscript", "eps", "mimetype-pps", true);
            RegisterMimeType("application/msaccess", "mdb", "mimetype-mdb", true);
            RegisterMimeType(Pdf, "pdf", "mimetype-pdf", true);
            RegisterMimeType("application/vnd.ms-powerpoint", "ppt", "mimetype-ppt", true);
            RegisterMimeType("application/vnd.openxmlformats-officedocument.presentationml.presentation", "pptx", "mimetype-ppt", true);
            RegisterMimeType("application/msword", "doc", "mimetype-doc", true);
            RegisterMimeType("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx", "mimetype-doc", true);
            RegisterMimeType("application/rtf", "rtf", "mimetype-rtf", true);
            RegisterMimeType("application/vnd.visio", "vsd", "mimetype-vsd", true);
            RegisterMimeType("application/x-font-woff", "woff");
            RegisterMimeType("application/vnd.ms-excel", "xls", "mimetype-xls", true);
            RegisterMimeType("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx", "mimetype-xls", true);

            RegisterMimeType(MimeTypeInfo.QuickTime, "mov", "mimetype-mov", true);
            RegisterMimeType(MimeTypeInfo.Flash, "swf", "mimetype-swf", true);
            RegisterMimeType(MimeTypeInfo.Director, new[] { "dcr", "dir" }, "mimetype-dir");

            RegisterMimeType("text/xml", new[] { "xml", "config", "xsl", "xslt" }, "mimetype-xml");

            const string mimeTypeZip = "application/zip";
            _toCanonical.Add("application/x-zip-compressed", mimeTypeZip);
            RegisterMimeType(mimeTypeZip, "zip", "mimetype-zip", true);

            
            _toCanonical.Add("text/txt", "text/plain");
            _toCanonical.Add("text/text", "text/plain");
            RegisterMimeType("text/plain", "txt", "mimetype-txt", true);


            // .Cs and asp.net files
            RegisterMimeType(MimeTypeInfo.Ascx, "ascx", "mimetype-ascx");
            RegisterMimeType(MimeTypeInfo.Aspx, "aspx", "mimetype-aspx");
            RegisterMimeType(MimeTypeInfo.Asax, "asax", "mimetype-asax");
            RegisterMimeType(MimeTypeInfo.Ashx, "ashx");
            RegisterMimeType(MimeTypeInfo.Asmx, "asmx");
            RegisterMimeType(MimeTypeInfo.MasterPage, "master");

            RegisterMimeType(MimeTypeInfo.CSharp, "cs");
            RegisterMimeType(MimeTypeInfo.CsHtml, "cshtml", "mimetype-cshtml");

            
            
            AddExtensionMapping("mp4", "video/mp4");
            AddExtensionMapping("ogg", "audio/ogg");
            AddExtensionMapping("ogv", "video/ogg");
            AddExtensionMapping("webm", "video/webm");
            AddExtensionMapping("svg", "image/svg+xml");
            AddExtensionMapping("svgz", "image/svg+xml");
            AddExtensionMapping("flv4", "video/mp4");
            AddExtensionMapping("eot", "application/vnd.ms-fontobject");
        }

        private static void RegisterMimeType(string canonicalMimeTypeName, string extension, string resourceName = null, bool iisServable = false)
        {
            RegisterMimeType(canonicalMimeTypeName, new [] { extension }, resourceName, iisServable);
        }

        private static void RegisterMimeType(string canonicalMimeTypeName, string[] extensions, string resourceName, bool iisServable = false)
        {
            _toCanonical.Add(canonicalMimeTypeName, canonicalMimeTypeName);

            foreach(string extension in extensions)
            {
                AddExtensionMapping(extension, canonicalMimeTypeName);
            }

            if(resourceName != null)
            {
                _mimeTypeToResourceName.Add(canonicalMimeTypeName, resourceName);
            }

            if (iisServable)
            {
                _iisServeableTypes.Add(canonicalMimeTypeName);
            }
        }

        private static bool AddExtensionMapping(string extension, string mimeType)
        {
            _mimeTypeToExtension[mimeType] = extension;

            if (!_extensionToCanonical.ContainsKey(extension))
            {
                _extensionToCanonical.Add(extension, mimeType);
                return true;
            }

            return false;
        }


        [SuppressMessage("Composite.IO", "Composite.DoNotUseConfigurationClass:DoNotUseConfigurationClass")]
        private static void LoadExtensionMappingsFromWebConfig()
	    {
            ConfigurationSection config;
            try
            {
                config = WebConfigurationManager.OpenWebConfiguration(HostingEnvironment.ApplicationVirtualPath).GetSection("system.webServer");
            }
            catch
	        {
                // Silent
                return;
	        }

            var configRawXml = config?.SectionInformation.GetRawXml();
            if (configRawXml == null)
            {
                return;
            }

            XElement webServerConfig = XElement.Parse(configRawXml);
            XElement staticContentConfig = webServerConfig.Element("staticContent");
            if(staticContentConfig == null)
            {
                return;
            }

            foreach(XElement mimeMapping in staticContentConfig.Elements("mimeMap"))
            {
                string extension = mimeMapping.Attribute("fileExtension").Value.ToLowerInvariant();
                string mimeType = mimeMapping.Attribute("mimeType").Value;

                if(extension.StartsWith("."))
                {
                    extension = extension.Substring(1);
                }

                if (!AddExtensionMapping(extension, mimeType))
                {
                    Log.LogWarning(typeof(MimeTypeInfo).Name, "MimeType for extension '{0}' has already been defined", extension);
                }
                else
	            {
                    _iisServeableTypes.Add(mimeType);
	            }
            }
	    }

        /// <exclude />
        public static string GetCanonical(string mimeType)
        {
            if (string.IsNullOrEmpty(mimeType))
            {
                return MimeTypeInfo.Default;
            }

            mimeType = mimeType.ToLowerInvariant();

            if (_toCanonical.ContainsKey(mimeType))
            {
                return _toCanonical[mimeType];
            }

            return mimeType;
        }



        /// <exclude />
        public static ResourceHandle GetResourceHandleFromMimeType(string mimeType)
        {
            if (mimeType != null && _mimeTypeToResourceName.ContainsKey(mimeType))
            {
                return GetIconHandle(_mimeTypeToResourceName[mimeType]);
            }
            return GetIconHandle("mimetype-unknown");
        }


        /// <exclude />
        public static string GetExtensionFromMimeType(string mimeType)
        {
            return _mimeTypeToExtension.TryGetValue(mimeType, out string extension) ? extension : null;
        }


        /// <exclude />
        public static string GetCanonicalFromExtension(string extension)
        {
            if (extension == null)
            {
                return MimeTypeInfo.Default;
            }

            extension = extension.ToLowerInvariant();

            if (extension.StartsWith("."))
            {
                extension = extension.Substring(1);
            }

            string mimeType;
            if (_extensionToCanonical.TryGetValue(extension, out mimeType))
            {
                return mimeType;
            }

            string fileName = "filename." + extension;
            return MimeMapping.GetMimeMapping(fileName);
        }

        /// <exclude />
        public static string GetMimeType(UploadedFile uploadedFile)
        {
            string fileName = System.IO.Path.GetFileName(uploadedFile.FileName);

            string mimeTypeFromExtension = GetCanonicalFromExtension(System.IO.Path.GetExtension(fileName));
            if (mimeTypeFromExtension != MimeTypeInfo.Default)
            {
                Log.LogInformation(LogTitle, $"Uploading file '{fileName}'. MIME type from extension: '{mimeTypeFromExtension}'");

                return mimeTypeFromExtension;
            }
            
            string mimeTypeFromBrowser = GetCanonical(uploadedFile.ContentType);

            // Default MIME type for Chrome is "application/xml"
            // Default MIME type for IE is "text/plain"
            // for the rest it is "application/octet-stream"
            if (mimeTypeFromBrowser != "application/xml"
                && mimeTypeFromBrowser != "text/plain")
            {
                Log.LogInformation(LogTitle, $"Uploading file '{fileName}'. " +
                    $"Browser provided MIME type: '{uploadedFile.ContentType}'. " +
                    $"Canonical MIME type: '{mimeTypeFromBrowser}'");

                return mimeTypeFromBrowser;
            }

            Log.LogInformation(LogTitle, $"Uploading file '{fileName}'. Applying default MIME type '{Default}'");

            return MimeTypeInfo.Default;
        }


        /// <summary>
        /// Indicates whether a file of a specific MIME type can be edited with a text editor
        /// </summary>
        /// <param name="mimeType"></param>
        /// <returns></returns>
        public static bool IsTextFile(string mimeType)
        {
            string canonicalMimeType = GetCanonical(mimeType);

            return canonicalMimeType.StartsWith("text") || _textMimeTypes.Contains(canonicalMimeType);
        }

        /// <summary>
        /// Indicates whether a file of a given mime type can be previewed by browser in an iframe.
        /// </summary>
        /// <param name="mimeType"></param>
        /// <returns></returns>
        public static bool IsBrowserPreviewableFile(string mimeType)
        {
            return mimeType == Pdf
                   || mimeType == Html
                   || mimeType == Text;
        }

        internal static string TryGetLocalizedName(string mimeType)
        {
            return StringResourceSystemFacade.GetString("MimeTypes", mimeType, false);
        }

        /// <summary>
        /// Indicates whether a file of a specific extension is expected to be allowed by IIS
        /// </summary>
        /// <param name="extension">The extension.</param>
        /// <returns>
        ///   <c>true</c> if the extension is 'IIS serveable'; otherwise, <c>false</c>.
        /// </returns>
        internal static bool IsIisServeable(string extension)
        {
            extension = extension.ToLowerInvariant();

            if (extension.StartsWith("."))
            {
                extension = extension.Substring(1);
            }

            return _iisServeableExtensions.GetOrAdd(extension, ext =>
            {
                string mimeType = GetCanonicalFromExtension(extension);
                return _iisServeableTypes.Contains(mimeType);
            });
        }
    }
}
