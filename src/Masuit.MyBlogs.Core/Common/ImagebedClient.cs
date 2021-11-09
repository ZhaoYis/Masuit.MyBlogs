﻿using Hangfire;
using Masuit.MyBlogs.Core.Configs;
using Masuit.Tools;
using Masuit.Tools.Html;
using Masuit.Tools.Logging;
using Masuit.Tools.Systems;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Masuit.MyBlogs.Core.Common
{
    /// <summary>
    /// 图床客户端
    /// </summary>
    public class ImagebedClient
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        /// <summary>
        /// 图床客户端
        /// </summary>
        /// <param name="httpClient"></param>
        /// <param name="config"></param>
        public ImagebedClient(HttpClient httpClient, IConfiguration config)
        {
            _config = config;
            _httpClient = httpClient;
        }

        private readonly List<string> _failedList = new();

        /// <summary>
        /// 上传图片
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="file"></param>
        /// <returns></returns>
        public Task<(string url, bool success)> UploadImage(Stream stream, string file, CancellationToken cancellationToken)
        {
            if (stream.Length < 51200)
            {
                return Task.FromResult<(string, bool)>((null, false));
            }

            file = Path.GetFileName(file);
            var gitlabs = AppConfig.GitlabConfigs.Where(c => c.FileLimitSize >= stream.Length && !_failedList.Contains(c.ApiUrl)).OrderByRandom().ToList();
            if (gitlabs.Count > 0)
            {
                var gitlab = gitlabs[0];
                if (gitlab.ApiUrl.Contains("gitee.com"))
                {
                    return UploadGitee(gitlab, stream, file, cancellationToken);
                }

                if (gitlab.ApiUrl.Contains("api.github.com"))
                {
                    return UploadGithub(gitlab, stream, file, cancellationToken);
                }

                return UploadGitlab(gitlab, stream, file, cancellationToken);
            }

            return UploadKieng(stream, cancellationToken);
        }

        /// <summary>
        /// 码云图床
        /// </summary>
        /// <param name="config"></param>
        /// <param name="stream"></param>
        /// <param name="file"></param>
        /// <returns></returns>
        private Task<(string url, bool success)> UploadGitee(GitlabConfig config, Stream stream, string file, CancellationToken cancellationToken)
        {
            var path = $"{DateTime.Now:yyyy\\/MM\\/dd}/{file}";
            return _httpClient.PostAsJsonAsync(config.ApiUrl + HttpUtility.UrlEncode(path), new
            {
                access_token = config.AccessToken,
                content = Convert.ToBase64String(stream.ToArray()),
                message = SnowFlake.NewId
            }, cancellationToken).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    using var resp = t.Result;
                    using var content = resp.Content;
                    if (resp.IsSuccessStatusCode || content.ReadAsStringAsync().Result.Contains("already exists"))
                    {
                        return (config.RawUrl + path, true);
                    }
                }

                LogManager.Info("图片上传到gitee失败。");
                return UploadKieng(stream, cancellationToken).Result;
            });
        }

        /// <summary>
        /// github图床
        /// </summary>
        /// <param name="config"></param>
        /// <param name="stream"></param>
        /// <param name="file"></param>
        /// <returns></returns>
        private Task<(string url, bool success)> UploadGithub(GitlabConfig config, Stream stream, string file, CancellationToken cancellationToken)
        {
            var path = $"{DateTime.Now:yyyy\\/MM\\/dd}/{file}";
            _httpClient.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse("Awesome-Octocat-App"));
            _httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse("token " + config.AccessToken);
            return _httpClient.PutAsJsonAsync(config.ApiUrl + HttpUtility.UrlEncode(path), new
            {
                message = SnowFlake.NewId,
                committer = new
                {
                    name = SnowFlake.NewId,
                    email = "1@1.cn"
                },
                content = Convert.ToBase64String(stream.ToArray())
            }, cancellationToken).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    using var resp = t.Result;
                    using var content = resp.Content;
                    if (resp.IsSuccessStatusCode)
                    {
                        return (config.RawUrl.Split(',').OrderByRandom().FirstOrDefault() + path, true);
                    }
                }

                LogManager.Info("图片上传到gitee失败。");
                return UploadKieng(stream, cancellationToken).Result;
            });
        }

        /// <summary>
        /// github图床
        /// </summary>
        /// <param name="config"></param>
        /// <param name="stream"></param>
        /// <param name="file"></param>
        /// <returns></returns>
        private Task<(string url, bool success)> UploadGitlab(GitlabConfig config, Stream stream, string file, CancellationToken cancellationToken)
        {
            var path = $"{DateTime.Now:yyyy\\/MM\\/dd}/{file}";
            _httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", config.AccessToken);
            return _httpClient.PostAsJsonAsync(config.ApiUrl.Contains("/v3/") ? config.ApiUrl : config.ApiUrl + HttpUtility.UrlEncode(path), new
            {
                file_path = path,
                branch_name = config.Branch,
                branch = config.Branch,
                author_email = CommonHelper.SystemSettings["ReceiveEmail"],
                author_name = SnowFlake.NewId,
                encoding = "base64",
                content = Convert.ToBase64String(stream.ToArray()),
                commit_message = SnowFlake.NewId
            }, cancellationToken).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    using var resp = t.Result;
                    using var content = resp.Content;
                    if (resp.IsSuccessStatusCode || content.ReadAsStringAsync().Result.Contains("already exists"))
                    {
                        return (config.RawUrl + path, true);
                    }
                }

                LogManager.Info($"图片上传到gitlab({config.ApiUrl})失败。");
                _failedList.Add(config.ApiUrl);
                return UploadKieng(stream, cancellationToken).Result;
            });
        }

        /// <summary>
        /// 上传到聚合图床
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<(string url, bool success)> UploadKieng(Stream stream, CancellationToken cancellationToken)
        {
            if (bool.TryParse(_config["Imgbed:EnableExternalImgbed"], out var b) && b)
            {
                using var formData = new MultipartFormDataContent
                {
                    { new StreamContent(stream), "image","1.jpg" }
                };
                var resp = await _httpClient.PostAsync("https://image.kieng.cn/upload.html?type=" + new[] { "jd", "c58", "sg", "sh", "wy" }.OrderByRandom().First(), formData, cancellationToken);
                var json = await resp.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);
                return ((string)result["data"]["url"], (int)result["code"] == 200);
            }

            return (null, false);
        }

        /// <summary>
        /// 替换img标签的src属性
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        public async Task<string> ReplaceImgSrc(string content, CancellationToken cancellationToken)
        {
            if (bool.TryParse(_config["Imgbed:EnableLocalStorage"], out var b) && b)
            {
                return content;
            }

            var srcs = content.MatchImgSrcs();
            foreach (var src in srcs)
            {
                if (src.StartsWith("http"))
                {
                    continue;
                }

                var path = Path.Combine(AppContext.BaseDirectory + "wwwroot", src.Replace("/", @"\")[1..]);
                if (!File.Exists(path))
                {
                    continue;
                }

                await using var stream = File.OpenRead(path);
                var (url, success) = await UploadImage(stream, path, cancellationToken);
                if (success)
                {
                    content = content.Replace(src, url);
                    BackgroundJob.Enqueue(() => File.Delete(path));
                }
            }

            return content;
        }
    }
}