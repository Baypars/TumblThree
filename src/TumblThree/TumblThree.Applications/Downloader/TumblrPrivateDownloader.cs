﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models;

using Post = TumblThree.Applications.DataModels.Post;

namespace TumblThree.Applications.Downloader
{
    [Export(typeof(IDownloader))]
    [ExportMetadata("BlogType", BlogTypes.tmblrpriv)]
    public class TumblrPrivateDownloader : Downloader, IDownloader
    {
        private int numberOfPagesCrawled = 0;

        public TumblrPrivateDownloader(IShellService shellService, CancellationToken ct, PauseToken pt, IProgress<DownloadProgress> progress, PostCounter counter, FileDownloader fileDownloader, ICrawlerService crawlerService, IBlog blog, IFiles files)
            : base(shellService, ct, pt, progress, counter, fileDownloader, crawlerService, blog, files)
        {
        }

        public override async Task IsBlogOnlineAsync()
        {
            try
            {
                string document = await GetSvcPageAsync("1", "0");
                blog.Online = true;
            }
            catch (WebException)
            {
                blog.Online = false;
            }
        }

        public override async Task UpdateMetaInformationAsync()
        {
            try
            {
                if (blog.Online)
                {
                    string document = await GetSvcPageAsync("1", "0");
                    var response = ConvertJsonToClass<TumblrJson>(document);

                    if (response.meta.status == 200)
                    {
                        blog.Title = response.response.posts.FirstOrDefault().blog.title;
                        blog.Description = response.response.posts.FirstOrDefault().blog.description;
                    }
                }
            }
            catch (WebException webException)
            {
                var webRespStatusCode = (int)((HttpWebResponse)webException?.Response).StatusCode;
                if (webRespStatusCode == 503)
                {
                    Logger.Error("TumblrDownloader:GetUrlsAsync: {0}", "User not logged in");
                    shellService.ShowError(new Exception("User not logged in"), Resources.NotLoggedIn, blog.Name);
                }
                else
                {
                    blog.Online = false;
                }
            }
        }

        public async Task Crawl()
        {
            Logger.Verbose("TumblrDownloader.Crawl:Start");

            Task grabber = GetUrlsAsync();
            Task<bool> downloader = DownloadBlogAsync();

            await grabber;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);
            blog.DuplicatePhotos = DetermineDuplicates(PostTypes.Photo);
            blog.DuplicateVideos = DetermineDuplicates(PostTypes.Video);
            blog.DuplicateAudios = DetermineDuplicates(PostTypes.Audio);
            blog.TotalCount = (blog.TotalCount - blog.DuplicatePhotos - blog.DuplicateAudios - blog.DuplicateVideos);

            CleanCollectedBlogStatistics();

            await downloader;

            if (!ct.IsCancellationRequested)
            {
                blog.LastCompleteCrawl = DateTime.Now;
            }

            blog.Save();

            UpdateProgressQueueInformation("");
        }

        private string ImageSize()
        {
            if (shellService.Settings.ImageSize == "raw")
                return "1280";
            return shellService.Settings.ImageSize;
        }

        private string ResizeTumblrImageUrl(string imageUrl)
        {
            var sb = new StringBuilder(imageUrl);
            return sb
                .Replace("_raw", "_" + ImageSize())
                .Replace("_1280", "_" + ImageSize())
                .Replace("_540", "_" + ImageSize())
                .Replace("_500", "_" + ImageSize())
                .Replace("_400", "_" + ImageSize())
                .Replace("_250", "_" + ImageSize())
                .Replace("_100", "_" + ImageSize())
                .Replace("_75sq", "_" + ImageSize())
                .ToString();
        }

        protected override bool CheckIfFileExistsInDirectory(string url)
        {
            string fileName = url.Split('/').Last();
            Monitor.Enter(lockObjectDirectory);
            string blogPath = blog.DownloadLocation();
            if (Directory.EnumerateFiles(blogPath).Any(file => file.Contains(fileName)))
            {
                Monitor.Exit(lockObjectDirectory);
                return true;
            }
            Monitor.Exit(lockObjectDirectory);
            return false;
        }

        private int DetermineDuplicates(PostTypes type)
        {
            return statisticsBag.Where(url => url.PostType.Equals(type))
                                .GroupBy(url => url.Url)
                                .Where(g => g.Count() > 1)
                                .Sum(g => g.Count() - 1);
        }

        private IEnumerable<int> GetPageNumbers()
        {
            if (!TestRange(blog.PageSize, 1, 100))
                blog.PageSize = 100;

            if (string.IsNullOrEmpty(blog.DownloadPages))
            {
                return Enumerable.Range(0, shellService.Settings.ParallelScans);
            }
            return RangeToSequence(blog.DownloadPages);
        }

        private static bool TestRange(int numberToCheck, int bottom, int top)
        {
            return (numberToCheck >= bottom && numberToCheck <= top);
        }

        static IEnumerable<int> RangeToSequence(string input)
        {
            string[] parts = input.Split(',');
            foreach (string part in parts)
            {
                if (!part.Contains('-'))
                {
                    yield return int.Parse(part);
                    continue;
                }
                string[] rangeParts = part.Split('-');
                int start = int.Parse(rangeParts[0]);
                int end = int.Parse(rangeParts[1]);

                while (start <= end)
                {
                    yield return start;
                    start++;
                }
            }
        }

        private async Task<ulong> GetHighestPostId()
        {
            string document = await GetSvcPageAsync("1", "0");
            var response = ConvertJsonToClass<TumblrJson>(document);

            ulong highestId;
            ulong.TryParse(blog.Title = response.response.posts.FirstOrDefault().id, out highestId);
            return highestId;
        }

        private ulong GetLastPostId()
        {
            ulong lastId = blog.LastId;
            if (blog.ForceRescan)
            {
                return 0;
            }
            if (!string.IsNullOrEmpty(blog.DownloadPages))
            {
                return 0;
            }
            return lastId;
        }

        private static bool CheckPostAge(TumblrJson document, ulong lastId)
        {
            ulong highestPostId = 0;
            ulong.TryParse(document.response.posts.FirstOrDefault().id,
                out highestPostId);

            if (highestPostId < lastId)
            {
                return false;
            }
            return true;
        }

        protected override bool CheckIfFileExistsInDB(string url)
        {
            string fileName = url.Split('/').Last();
            Monitor.Enter(lockObjectDb);
            if (files.Links.Contains(fileName))
            {
                Monitor.Exit(lockObjectDb);
                return true;
            }
            Monitor.Exit(lockObjectDb);
            return false;
        }

        private async Task GetUrlsAsync()
        {
            var semaphoreSlim = new SemaphoreSlim(shellService.Settings.ParallelScans);
            var trackedTasks = new List<Task>();

            foreach (int crawlerNumber in Enumerable.Range(0, shellService.Settings.ParallelScans))
            {
                await semaphoreSlim.WaitAsync();

                trackedTasks.Add(new Func<Task>(async () =>
                {
                    var tags = new List<string>();
                    if (!string.IsNullOrWhiteSpace(blog.Tags))
                    {
                        tags = blog.Tags.Split(',').Select(x => x.Trim()).ToList();
                    }

                    try
                    {
                        string document = await GetSvcPageAsync(blog.PageSize.ToString(), (blog.PageSize * crawlerNumber).ToString());
                        var response = ConvertJsonToClass<TumblrJson>(document);
                        await AddUrlsToDownloadList(response, tags, crawlerNumber);
                    }
                    catch (WebException webException)
                    {
                        if (webException.Message.Contains("503"))
                        {
                            Logger.Error("TumblrDownloader:GetUrlsAsync: {0}", "User not logged in");
                            shellService.ShowError(new Exception("User not logged in"), Resources.NotLoggedIn, blog.Name);
                            return;
                        }
                        if (webException.Message.Contains("429"))
                        {
                            // TODO: add retry logic?
                            Logger.Error("TumblrDownloader:GetUrls:WebException {0}", webException);
                            shellService.ShowError(webException, Resources.LimitExceeded, blog.Name);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        semaphoreSlim.Release();
                    }
                })());
            }
            await Task.WhenAll(trackedTasks);

            producerConsumerCollection.CompleteAdding();

            if (!ct.IsCancellationRequested)
            {
                UpdateBlogStats();
            }
        }

        private async Task<string> GetSvcPageAsync(string limit, string offset)
        {
            if (shellService.Settings.LimitConnections)
            {
                return await RequestDataAsync(limit, offset);
            }
            return await RequestDataAsync(limit, offset);
        }

        protected virtual async Task<string> RequestDataAsync(string limit, string offset)
        {
            var requestRegistration = new CancellationTokenRegistration();
            try
            {
                HttpWebRequest request = CreateWebReqeust(limit, offset);
                requestRegistration = ct.Register(() => request.Abort());
                using (var response = await request.GetResponseAsync() as HttpWebResponse)
                {
                    using (var stream = GetStreamForApiRequest(response.GetResponseStream()))
                    {
                        using (var buffer = new BufferedStream(stream))
                        {
                            using (var reader = new StreamReader(buffer))
                            {
                                return reader.ReadToEnd();
                            }
                        }
                    }
                }
            }
            finally
            {
                requestRegistration.Dispose();
            }
        }

        protected HttpWebRequest CreateWebReqeust(string limit, string offset)
        {
            string url = @"https://www.tumblr.com/svc/indash_blog?tumblelog_name_or_id=" + blog.Name +
                  @"&post_id=&limit=" + limit + "&offset=" + offset + "&should_bypass_safemode=true";
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.ContentType = "application/json";
            request.ProtocolVersion = HttpVersion.Version11;
            request.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/56.0.2924.87 Safari/537.36";
            request.AllowAutoRedirect = true;
            request.KeepAlive = true;
            request.Pipelined = true;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            // Timeouts don't work with GetResponseAsync() as it internally uses BeginGetResponse.
            // See docs: https://msdn.microsoft.com/en-us/library/system.net.httpwebrequest.timeout(v=vs.110).aspx
            // Quote: The Timeout property has no effect on asynchronous requests made with the BeginGetResponse or BeginGetRequestStream method.
            // TODO: Use HttpClient instead?
            request.ReadWriteTimeout = shellService.Settings.TimeOut * 1000;
            request.Timeout = -1;
            request.CookieContainer = SharedCookieService.GetUriCookieContainer(new Uri("https://www.tumblr.com/"));
            ServicePointManager.DefaultConnectionLimit = 400;
            request = SetWebRequestProxy(request, shellService.Settings);
            request.Referer = @"https://www.tumblr.com/dashboard/blog/" + blog.Name;
            request.Headers["X-Requested-With"] = "XMLHttpRequest";
            return request;
        }

        private async Task AddUrlsToDownloadList(TumblrJson response, IList<string> tags, int crawlerNumber)
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                if (pt.IsPaused)
                {
                    pt.WaitWhilePausedWithResponseAsyc().Wait();
                }

                try
                {
                    AddPhotoUrlToDownloadList(response, tags);
                    AddVideoUrlToDownloadList(response, tags);
                    AddAudioUrlToDownloadList(response, tags);
                    AddTextUrlToDownloadList(response, tags);
                    AddQuoteUrlToDownloadList(response, tags);
                    AddLinkUrlToDownloadList(response, tags);
                    AddConversationUrlToDownloadList(response, tags);
                    AddAnswerUrlToDownloadList(response, tags);
                    AddPhotoMetaUrlToDownloadList(response, tags);
                    AddVideoMetaUrlToDownloadList(response, tags);
                    AddAudioMetaUrlToDownloadList(response, tags);
                }
                catch (NullReferenceException)
                {
                }

                Interlocked.Increment(ref numberOfPagesCrawled);
                UpdateProgressQueueInformation(Resources.ProgressGetUrlShort, numberOfPagesCrawled);

                string document = await GetSvcPageAsync(blog.PageSize.ToString(), (blog.PageSize * crawlerNumber).ToString());
                response = ConvertJsonToClass<TumblrJson>(document);
                if (!response.response.posts.Any())
                {
                    return;
                }

                crawlerNumber += shellService.Settings.ParallelScans;
            }
        }

        public static T ConvertJsonToClass<T>(string json)
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            return serializer.Deserialize<T>(json);
        }

        private bool CheckIfDownloadRebloggedPosts(Post post)
        {
            if (!blog.DownloadRebloggedPosts)
            {
                if (!post.reblogged_from_url.Any())
                    return true;
                return false;
            }
            return true;
        }

        protected override async Task DownloadPhotoAsync(TumblrPost downloadItem)
        {
            string url = Url(downloadItem);

            if (blog.ForceSize)
            {
                url = ResizeTumblrImageUrl(url);
            }

            foreach (string host in shellService.Settings.TumblrHosts)
            {
                url = BuildRawImageUrl(url, host);
                if (await DownloadDetectedImageUrl(url, PostDate(downloadItem)))
                    return;
            }

            await DownloadDetectedImageUrl(Url(downloadItem), PostDate(downloadItem));
        }

        private async Task<bool> DownloadDetectedImageUrl(string url, DateTime postDate)
        {
            if (!(CheckIfFileExistsInDB(url) || CheckIfBlogShouldCheckDirectory(GetCoreImageUrl(url))))
            {
                string blogDownloadLocation = blog.DownloadLocation();
                string fileName = url.Split('/').Last();
                string fileLocation = FileLocation(blogDownloadLocation, fileName);
                string fileLocationUrlList = FileLocationLocalized(blogDownloadLocation, Resources.FileNamePhotos);
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, fileName);
                if (await DownloadBinaryFile(fileLocation, fileLocationUrlList, url))
                {
                    SetFileDate(fileLocation, postDate);
                    UpdateBlogPostCount(ref counter.Photos, value => blog.DownloadedPhotos = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(fileName);
                    if (shellService.Settings.EnablePreview)
                    {
                        if (!fileName.EndsWith(".gif"))
                        {
                            blog.LastDownloadedPhoto = Path.GetFullPath(fileLocation);
                        }
                        else
                        {
                            blog.LastDownloadedVideo = Path.GetFullPath(fileLocation);
                        }
                    }
                    return true;
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Builds a tumblr raw image url from any sized tumblr image url if the ImageSize is set to "raw".
        /// </summary>
        /// <param name="url">The url detected from the crawler.</param>
        /// <param name="host">Hostname to insert in the original url.</param>
        /// <returns></returns>
        public string BuildRawImageUrl(string url, string host)
        {
            if (shellService.Settings.ImageSize == "raw")
            {
                string path = new Uri(url).LocalPath.TrimStart('/');
                var imageDimension = new Regex("_\\d+");
                path = imageDimension.Replace(path, "_raw");
                return "https://" + host + "/" + path;
            }
            return url;
        }

        private void AddPhotoUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadPhoto)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "photo" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            AddPhotoUrl(post);
                        }
                    }
                    // check for inline images
                    //if (post.type != "photo" && !tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any())
                    //{
                    //    if (CheckIfDownloadRebloggedPosts(post))
                    //        try { AddInlinePhotoUrl(post); }
                    //        catch { }
                    //}
                }
            }
        }

        private void AddPhotoUrl(Post post)
        {
            string postId = post.id;
            foreach (Photo photo in post.photos)
            {
                string imageUrl = photo.alt_sizes.Where(url => url.width == int.Parse(ImageSize())).Select(url => url.url).FirstOrDefault();
                if (imageUrl == null)
                    imageUrl = photo.alt_sizes.FirstOrDefault().url;

                if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                {
                    continue;
                }

                AddToDownloadList(new TumblrPost(PostTypes.Photo, imageUrl, postId, post.timestamp.ToString()));
            }
        }

        private void AddInlinePhotoUrl(Post post)
        {
            var regex = new Regex("\"(http[\\S]*media.tumblr.com[\\S]*(jpg|png|gif))\"");
            foreach (Match match in regex.Matches(post.body))
            {
                string postId = post.id;

                string imageUrl = match.Groups[1].Value;
                if (imageUrl.Contains("avatar") || imageUrl.Contains("previews"))
                    continue;
                if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                {
                    continue;
                }
                AddToDownloadList(new TumblrPost(PostTypes.Photo, imageUrl, postId, post.timestamp.ToString()));
            }
        }


        private void AddVideoUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadVideo)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "video" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            AddVideoUrl(post);
                        }
                    }
                    // check for inline videos
                    //if (post.type != "video" && !tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any())
                    //{
                    //    if (CheckIfDownloadRebloggedPosts(post))
                    //        try { AddInlineVideoUrl(post); }
                    //        catch { }
                    //}
                }
            }
        }

        private void AddVideoUrl(Post post)
        {
            string postId = post.id;
            string videoUrl = post.video_url;

            if (shellService.Settings.VideoSize == 480)
            {
                if (!videoUrl.Contains("_480"))
                {
                    videoUrl.Replace(".mp4", "_480.mp4");
                }
            }
            AddToDownloadList(new TumblrPost(PostTypes.Video, videoUrl, postId, post.timestamp.ToString()));
        }

        private void AddInlineVideoUrl(Post post)
        {
            var regex = new Regex("\"(http[\\S]*.com/video_file/[\\S]*)\"");
            foreach (Match match in regex.Matches(post.body))
            {
                string videoUrl = match.Groups[1].Value;
                if (shellService.Settings.VideoSize == 1080)
                {
                    AddToDownloadList(new TumblrPost(PostTypes.Video, videoUrl.Replace("/480", "") + ".mp4", post.id, post.timestamp.ToString()));
                }
                else if (shellService.Settings.VideoSize == 480)
                {
                    AddToDownloadList(new TumblrPost(PostTypes.Video,
                        "https://vt.tumblr.com/" + videoUrl.Replace("/480", "").Split('/').Last() + "_480.mp4",
                        post.id, post.timestamp.ToString()));
                }
            }
        }

        private void AddAudioUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadAudio)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "audio" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string audioUrl = post.audio_url;
                            if (!audioUrl.EndsWith(".mp3"))
                                audioUrl = audioUrl + ".mp3";
                            AddToDownloadList(new TumblrPost(PostTypes.Audio, audioUrl, postId, post.timestamp.ToString()));
                        }
                    }
                }
            }
        }

        private void AddTextUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadText)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "text" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParseText(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Text, textBody, postId, post.timestamp.ToString()));
                        }
                    }
                }
            }
        }

        private void AddQuoteUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadQuote)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "quote" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParseQuote(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Quote, textBody, postId, post.timestamp.ToString()));
                        }
                    }
                }
            }
        }

        private void AddLinkUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadLink)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "link" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParseLink(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Link, textBody, postId, post.timestamp.ToString()));
                        }
                    }
                }
            }
        }

        private void AddConversationUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadConversation)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "chat" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParseConversation(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Conversation, textBody, postId, post.timestamp.ToString()));
                        }
                    }
                }
            }
        }

        private void AddAnswerUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.DownloadAnswer)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "answer" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParseAnswer(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Answer, textBody, postId, post.timestamp.ToString()));
                        }
                    }
                }
            }
        }

        private void AddPhotoMetaUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.CreatePhotoMeta)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "photo" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParsePhotoMeta(post);
                            AddToDownloadList(new TumblrPost(PostTypes.PhotoMeta, textBody, postId));
                        }
                    }
                }
            }
        }

        private void AddVideoMetaUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.CreateVideoMeta)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "video" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParseVideoMeta(post);
                            AddToDownloadList(new TumblrPost(PostTypes.VideoMeta, textBody, postId));
                        }
                    }
                }
            }
        }

        private void AddAudioMetaUrlToDownloadList(TumblrJson document, IList<string> tags)
        {
            if (blog.CreateAudioMeta)
            {
                foreach (Post post in document.response.posts)
                {
                    if (post.type == "audio" && (!tags.Any() || post.tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string postId = post.id;
                            string textBody = ParseAudioMeta(post);
                            AddToDownloadList(new TumblrPost(PostTypes.AudioMeta, textBody, postId));
                        }
                    }
                }
            }
        }

        private static string ParseText(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Title, post.title) +
                   Environment.NewLine + post.body +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseQuote(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Quote, post.dialogue.Select(dialogue => dialogue.phrase).FirstOrDefault()) +
                   Environment.NewLine + post.body +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseLink(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Link, post.link_url) +
                   Environment.NewLine + post.body +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseConversation(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Quote, post.dialogue.Select(dialogue => dialogue.phrase).FirstOrDefault()) +
                   Environment.NewLine + post.body +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseAnswer(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   post.question +
                   Environment.NewLine +
                   post.answer +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private static string ParsePhotoMeta(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.PhotoUrl, post.photos.Select(photo => photo.original_size.url).FirstOrDefault()) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.PhotoCaption, post.photos.Select(photo => photo.caption).FirstOrDefault()) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseVideoMeta(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.VideoPlayer, post.caption) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseAudioMeta(Post post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.id) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.date) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.slug) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.reblog_key) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.reblogged_from_url) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.reblogged_from_name) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.AudioCaption, post.caption) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Artist, post.artist) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Title, post.title) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Track, post.track) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Album, post.album) +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Track, post.year) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.tags.ToArray())) +
                   Environment.NewLine;
        }

        private void UpdateBlogStats()
        {
            blog.TotalCount = statisticsBag.Count;
            blog.Photos = statisticsBag.Count(url => url.PostType.Equals(PostTypes.Photo));
            blog.Videos = statisticsBag.Count(url => url.PostType.Equals(PostTypes.Video));
            blog.Audios = statisticsBag.Count(url => url.PostType.Equals(PostTypes.Audio));
            blog.Texts = statisticsBag.Count(url => url.PostType.Equals(PostTypes.Text));
            blog.Conversations = statisticsBag.Count(url => url.PostType.Equals(PostTypes.Conversation));
            blog.Quotes = statisticsBag.Count(url => url.PostType.Equals(PostTypes.Quote));
            blog.NumberOfLinks = statisticsBag.Count(url => url.PostType.Equals(PostTypes.Link));
            blog.PhotoMetas = statisticsBag.Count(url => url.PostType.Equals(PostTypes.PhotoMeta));
            blog.VideoMetas = statisticsBag.Count(url => url.PostType.Equals(PostTypes.VideoMeta));
            blog.AudioMetas = statisticsBag.Count(url => url.PostType.Equals(PostTypes.AudioMeta));
        }

        private void AddToDownloadList(TumblrPost addToList)
        {
            producerConsumerCollection.Add(addToList);
            statisticsBag.Add(addToList);
        }
    }
}
