﻿using MediaBrowser.Common.IO;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Server.Implementations.LiveTv
{
    public class ProgramImageProvider : BaseMetadataProvider
    {
        private readonly ILiveTvManager _liveTvManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClient _httpClient;

        public ProgramImageProvider(ILogManager logManager, IServerConfigurationManager configurationManager, ILiveTvManager liveTvManager, IProviderManager providerManager, IFileSystem fileSystem, IHttpClient httpClient)
            : base(logManager, configurationManager)
        {
            _liveTvManager = liveTvManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _httpClient = httpClient;
        }

        public override bool Supports(BaseItem item)
        {
            return item is LiveTvProgram;
        }

        protected override bool NeedsRefreshInternal(BaseItem item, BaseProviderInfo providerInfo)
        {
            return !item.HasImage(ImageType.Primary);
        }

        public override async Task<bool> FetchAsync(BaseItem item, bool force, BaseProviderInfo providerInfo, CancellationToken cancellationToken)
        {
            if (item.HasImage(ImageType.Primary))
            {
                SetLastRefreshed(item, DateTime.UtcNow, providerInfo);
                return true;
            }

            try
            {
                await DownloadImage((LiveTvProgram)item, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpException ex)
            {
                // Don't fail the provider on a 404
                if (!ex.StatusCode.HasValue || ex.StatusCode.Value != HttpStatusCode.NotFound)
                {
                    throw;
                }
            }

            SetLastRefreshed(item, DateTime.UtcNow, providerInfo);
            return true;
        }

        private async Task DownloadImage(LiveTvProgram item, CancellationToken cancellationToken)
        {
            var programInfo = item.ProgramInfo;

            Stream imageStream = null;
            string contentType = null;

            if (!string.IsNullOrEmpty(programInfo.ImagePath))
            {
                contentType = "image/" + Path.GetExtension(programInfo.ImagePath).ToLower();
                imageStream = _fileSystem.GetFileStream(programInfo.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, true);
            }
            else if (!string.IsNullOrEmpty(programInfo.ImageUrl))
            {
                var options = new HttpRequestOptions
                {
                    CancellationToken = cancellationToken,
                    Url = programInfo.ImageUrl
                };

                var response = await _httpClient.GetResponse(options).ConfigureAwait(false);

                if (!response.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Provider did not return an image content type.");
                }

                imageStream = response.Content;
                contentType = response.ContentType;
            }
            else
            {
                var service = _liveTvManager.Services.FirstOrDefault(i => string.Equals(i.Name, item.ServiceName, StringComparison.OrdinalIgnoreCase));

                if (service != null)
                {
                    var response = await service.GetProgramImageAsync(programInfo.Id, programInfo.ChannelId, cancellationToken).ConfigureAwait(false);

                    imageStream = response.Stream;
                    contentType = response.MimeType;
                }
            }

            if (imageStream != null)
            {
                // Dummy up the original url
                var url = item.ServiceName + programInfo.Id;

                await _providerManager.SaveImage(item, imageStream, contentType, ImageType.Primary, null, url, cancellationToken).ConfigureAwait(false);
            }
        }

        public override MetadataProviderPriority Priority
        {
            get { return MetadataProviderPriority.Second; }
        }

        public override ItemUpdateType ItemUpdateType
        {
            get
            {
                return ItemUpdateType.ImageUpdate;
            }
        }
    }
}
