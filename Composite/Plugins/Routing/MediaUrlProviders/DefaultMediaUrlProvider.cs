using System;
using System.IO;
using System.Text;
using System.Web;
using Composite.Core;
using Composite.Core.IO;
using Composite.Core.Routing;
using Composite.Core.WebClient;
using Composite.Core.WebClient.Media;
using Composite.Data.Types;

namespace Composite.Plugins.Routing.MediaUrlProviders
{
    /// <exclude />
    public class DefaultMediaUrlProvider: IResizableImageUrlProvider
    {
        internal static readonly string DefaultMediaStore = "MediaArchive";
        private static readonly string ForbiddenUrlCharacters = @"<>*%&\?#""";

        private readonly string _storeId;

        /// <exclude />
        public DefaultMediaUrlProvider(string storeId)
        {
            _storeId = storeId;
        }


        /// <exclude />
        public bool IsSupportedStoreId(string storeId)
        {
            return storeId == _storeId;
        }

        /// <exclude />
        public string GetPublicMediaUrl(string storeId, Guid mediaId)
        {
            return GetResizedImageUrl(storeId, mediaId, null);
        }

        /// <exclude />
        public string GetResizedImageUrl(string storeId, Guid mediaId, ResizingOptions resizingOptions)
        {
            IMediaFile file = MediaUrlHelper.GetFileById(storeId, mediaId);
            if (file == null)
            {
                return null;
            }

            var fileName = resizingOptions != null && string.Equals(resizingOptions.FileExtension, ImageResizer.WebP, StringComparison.InvariantCultureIgnoreCase)
                ? $"{Path.GetFileNameWithoutExtension(file.FileName)}.{ImageResizer.WebP}"
                : file.FileName;

            string pathToFile = UrlUtils.Combine(file.FolderPath, fileName);

            pathToFile = RemoveForbiddenCharactersAndNormalize(pathToFile);

            // IIS6 doesn't have wildcard mapping by default, so removing image extension if running in "classic" app pool
            if (!HttpRuntime.UsingIntegratedPipeline)
            {
                int dotOffset = pathToFile.IndexOf(".", StringComparison.Ordinal);
                if (dotOffset >= 0)
                {
                    pathToFile = pathToFile.Substring(0, dotOffset);
                }
            }

            string mediaStore = string.Empty;

            if (!storeId.Equals(DefaultMediaStore, StringComparison.InvariantCultureIgnoreCase))
            {
                mediaStore = storeId + "/";
            }


            var url = new UrlBuilder(UrlUtils.PublicRootPath + "/media/" + mediaStore + /* UrlUtils.CompressGuid(*/ mediaId /*)*/)
            {
                PathInfo = file.LastWriteTime != null
                    ? "/" + GetDateTimeHash(file.LastWriteTime.Value.ToUniversalTime())
                    : string.Empty
            };

            if (pathToFile.Length > 0)
            {
                url.PathInfo += pathToFile;
            }

            if (resizingOptions != null && !resizingOptions.IsEmpty)
            {
                return url + "?" + resizingOptions;
            }

            return url.ToString();
        }


        private static string GetDateTimeHash(DateTime dateTime)
        {
            int hash = dateTime.GetHashCode();
            return Convert.ToBase64String(BitConverter.GetBytes(hash)).Substring(0, 6).Replace('+', '-').Replace('/', '_');
        }


        private static string RemoveForbiddenCharactersAndNormalize(string path)
        {
            // Replacing dots with underscores, so IIS will not intercept requests in some scenarios

            string legalFilePath = RemoveFilePathIllegalCharacters(path);
            string extension = Path.GetExtension(legalFilePath);

            if (!MimeTypeInfo.IsIisServeable(extension))
            {
                path = path.Replace('.', '_');
            }

            path = path.Replace('+', ' ');

            foreach (var ch in ForbiddenUrlCharacters)
            {
                path = path.Replace(ch, '#');
            }

            path = path.Replace("#", string.Empty);

            // Removing consecutive white spaces
            while (path.Contains("  "))
            {
                path = path.Replace("  ", " ");
            }

            string[] parts = path.Split('/');

            var result = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string trimmedPart = parts[i].Trim();
                if (trimmedPart.Length > 0)
                {
                    result.Append("/").Append(trimmedPart);
                }
            }

            // Encoding white spaces
            result.Replace(" ", "%20");

            return result.ToString();
        }


        private static string RemoveFilePathIllegalCharacters(string path)
        {
            path = path.Replace('\"', ' ').Replace('<', ' ').Replace('>', ' ').Replace('|', ' ');
            for (int i = 0; i < 31; i++)
            {
                path = path.Replace((char)i, ' ');
            }
            return path;
        }
    }
}
