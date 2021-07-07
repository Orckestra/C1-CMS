using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Caching;
using System.Xml.Linq;
using Composite.Core.IO;
using Composite.Core.Xml;
using Composite.Core.Configuration;

namespace Composite.Core.WebClient.Media
{
    /// <summary>    
    /// Resizing options for <see ref="Composite.Core.WebClient.Media.ImageResizer" />
    /// </summary>
    public class ResizingOptions
    {
        private const string ResizedImageKeys = "~/App_Data/Composite/Media/ResizingOptions.xml";
        private static string _resizedImageKeysFilePath;

        private int? _qualityOverride;

        /// <summary>
        /// Image heigth
        /// </summary>
        public int? Height { get; set; }

        /// <summary>
        /// Image width
        /// </summary>
        public int? Width { get; set; }

        /// <summary>
        /// Maximum height
        /// </summary>
        public int? MaxHeight { get; set; }

        /// <summary>
        /// Maximum width
        /// </summary>
        public int? MaxWidth { get; set; }

        /// <summary>
        /// Indicate if resizing options has a default or non-default quality setting (used when doing lossy compression). 
        /// </summary>
        public bool CustomQuality => _qualityOverride.HasValue;

        /// <summary>
        /// Image quality (when doing lossy compression)
        /// </summary>
        public int Quality
        {
            get => _qualityOverride ?? GlobalSettingsFacade.ImageQuality;

            set
            {
                _qualityOverride = value;

                if (_qualityOverride < 1)
                {
                    _qualityOverride = 1;
                }

                if (_qualityOverride > 100)
                {
                    _qualityOverride = 100;
                }
            }
        }

        /// <summary>
        /// FileExtension (for .webp extension)
        /// </summary>
        public string FileExtension { get; set; }

    /// <summary>
    /// Resizing action
    /// </summary>
    public ResizingAction ResizingAction { get; set; }

        /// <summary>
        /// Indicates whether any options were specified
        /// </summary>
        public bool IsEmpty => Height == null && Width == null && MaxHeight == null && MaxWidth == null && _qualityOverride == null;

        /// <exclude />
        public ResizingOptions() { }

        /// <summary>
        /// Parses resizing options from query string collection
        /// </summary>
        /// <param name="httpServerUtility">An instance of <see ref="System.Web.HttpServerUtility" />.</param>
        /// <param name="queryString">The query string.</param>
        /// <returns>Resizing options</returns>
        [Obsolete("Use an overload not taking an HttpServerUtility instance as a parameter")]
        public static ResizingOptions Parse(HttpServerUtility httpServerUtility, NameValueCollection queryString)
        {
            return Parse(queryString);
        }

        /// <summary>
        /// Parses resizing options from query string collection
        /// </summary>
        /// <param name="queryString">The query string.</param>
        /// <returns>Resizing options</returns>
        public static ResizingOptions Parse(NameValueCollection queryString)
        {
            var resizingKey = queryString["k"];

            return string.IsNullOrEmpty(resizingKey) ? FromQueryString(queryString) : new ResizingOptions(resizingKey);
        }

        /// <exclude />
        internal ResizingOptions(string predefinedOptionsName)
        {
            //Load the xml file
            var options = GetPredefinedResizingOptions().Elements("image");

            foreach (var e in options.Where(e => (string)e.Attribute("name") == predefinedOptionsName))
            {
                Height = ParseOptionalIntAttribute(e, "height");
                Width = ParseOptionalIntAttribute(e, "width");
                MaxHeight = ParseOptionalIntAttribute(e, "maxheight");
                MaxWidth = ParseOptionalIntAttribute(e, "maxwidth");
                _qualityOverride = ParseOptionalIntAttribute(e, "quality");

                var attr = e.Attribute("action");
                if (attr != null)
                {
                    ResizingAction = (ResizingAction)Enum.Parse(typeof(ResizingAction), attr.Value, true);
                }
            }
        }

        private static int? ParseOptionalIntAttribute(XElement element, string attributeName)
        {
            var attribute = element.Attribute(attributeName);

            return attribute == null ? (int?)null : int.Parse(attribute.Value);
        }

        /// <summary>
        /// Gets resizing options from query string
        /// </summary>
        /// <param name="queryString">The query string.</param>
        /// <returns>Resizing options</returns>
        private static ResizingOptions FromQueryString(NameValueCollection queryString)
        {
            var result = new ResizingOptions();

            var str = queryString["w"];
            if (!string.IsNullOrEmpty(str))
            {
                result.Width = int.Parse(str);
            }

            str = queryString["h"];
            if (!string.IsNullOrEmpty(str))
            {
                result.Height = int.Parse(str);
            }

            str = queryString["mw"];
            if (!string.IsNullOrEmpty(str))
            {
                result.MaxWidth = int.Parse(str);
            }

            str = queryString["mh"];
            if (!string.IsNullOrEmpty(str))
            {
                result.MaxHeight = int.Parse(str);
            }

            str = queryString["q"];
            if (!string.IsNullOrEmpty(str))
            {
                result.Quality = int.Parse(str);
            }

            str = queryString["ext"];
            if (!string.IsNullOrEmpty(str))
            {
                result.FileExtension = str;
            }

            var action = queryString["action"];
            if (!string.IsNullOrEmpty(action) && Enum.TryParse(action, true, out ResizingAction resizingAction))
            {
                result.ResizingAction = resizingAction;
            }
            else
            {
                result.ResizingAction = ResizingAction.Stretch;
            }

            return result;
        }
        private static XElement GetPredefinedResizingOptions()
        {
            //If it's not there, load the xml document and then add it to the cache
            if (!(HttpRuntime.Cache.Get("ResizedImageKeys") is XElement xel))
            {
                if (_resizedImageKeysFilePath == null)
                {
                    _resizedImageKeysFilePath = PathUtil.Resolve(ResizedImageKeys);
                }

                if (!C1File.Exists(_resizedImageKeysFilePath))
                {
                    var directoryPath = Path.GetDirectoryName(_resizedImageKeysFilePath);
                    if (!C1Directory.Exists(directoryPath)) C1Directory.CreateDirectory(directoryPath);

                    var config = new XElement("ResizedImages",
                        new XElement("image",
                            new XAttribute("name", "thumbnail"),
                            new XAttribute("maxwidth", "100"),
                            new XAttribute("maxheight", "100")),
                        new XElement("image",
                            new XAttribute("name", "normal"),
                            new XAttribute("maxwidth", "200")),
                        new XElement("image",
                            new XAttribute("name", "large"),
                            new XAttribute("maxheight", "300"))
                    );

                    config.SaveToPath(_resizedImageKeysFilePath);
                }

                xel = XElementUtils.Load(_resizedImageKeysFilePath);

                var cd = new CacheDependency(_resizedImageKeysFilePath);
                var cacheExpirationTimeSpan = new TimeSpan(24, 0, 0);

                HttpRuntime.Cache.Add("ResizedImageKeys", xel, cd, Cache.NoAbsoluteExpiration, cacheExpirationTimeSpan, CacheItemPriority.Default, null);
            }

            return xel;
        }

        /// <exclude />
        public override string ToString()
        {
            var sb = new StringBuilder();
            var parameters = new[] { Width, Height, MaxWidth, MaxHeight, _qualityOverride };
            var parameterNames = new[] { "w", "h", "mw", "mh", "q"};

            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i] == null)
                {
                    continue;
                }

                sb.Append(sb.Length == 0 ? String.Empty : "&");
                sb.Append(parameterNames[i]).Append("=").Append((int)parameters[i]);
            }

            if (string.Equals(FileExtension, "webp", StringComparison.InvariantCultureIgnoreCase))
            {
                sb.Append(sb.Length == 0 ? String.Empty : "&");
                sb.Append("ext=webp");
            }

            if (ResizingAction != ResizingAction.Stretch)
            {
                sb.Append(sb.Length == 0 ? String.Empty : "&");
                sb.Append("action=").Append(ResizingAction.ToString().ToLowerInvariant());
            }

            return sb.ToString();
        }
    }
}

