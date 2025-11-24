using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// 剪映 (JianYing/CapCut) ASR 服务实现
    /// 参考 VideoCaptioner 的 JianYingASR
    /// </summary>
    public class JianYingAsrService
    {
        private const string SIGN_SERVICE_URL = "https://asrtools-update.bkfeng.top/sign";
        private const string UPLOAD_SIGN_URL = "https://lv-pc-api-sinfonlinec.ulikecam.com/lv/v1/upload_sign";
        private const string SUBMIT_URL = "https://lv-pc-api-sinfonlinec.ulikecam.com/lv/v1/audio_subtitle/submit";
        private const string QUERY_URL = "https://lv-pc-api-sinfonlinec.ulikecam.com/lv/v1/audio_subtitle/query";
        private const string VOD_API_URL = "https://vod.bytedanceapi.com/";

        private readonly HttpClient _httpClient;
        private string _tdid = string.Empty;

        public JianYingAsrService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _tdid = GenerateTdid();
        }

        /// <summary>
        /// 执行 ASR 转录
        /// </summary>
        public async Task<string> TranscribeAsync(
            string audioFilePath,
            bool needWordTimeStamp = false,
            IProgress<(int progress, string message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var audioBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
            var crc32 = CalculateCrc32(audioBytes);

            // 1. 上传音频
            progress?.Report((10, "上传音频文件..."));
            var storeUri = await UploadAudioAsync(audioBytes, crc32, cancellationToken);

            // 2. 提交任务
            progress?.Report((40, "提交转录任务..."));
            var queryId = await SubmitTaskAsync(storeUri, cancellationToken);

            // 3. 查询结果
            progress?.Report((60, "等待转录完成..."));
            var result = await QueryResultAsync(queryId, progress, cancellationToken);

            // 4. 转换为 SRT
            progress?.Report((95, "生成字幕文件..."));
            return ConvertToSrt(result, needWordTimeStamp);
        }

        private async Task<string> UploadAudioAsync(byte[] audioBytes, string crc32, CancellationToken cancellationToken)
        {
            // 1. 获取上传签名
            var (accessKey, secretKey, sessionToken) = await GetUploadSignAsync(cancellationToken);

            // 2. 获取上传授权
            var (storeUri, auth, uploadId, sessionKey, uploadHost) = await GetUploadAuthAsync(
                audioBytes.Length, accessKey, secretKey, sessionToken, cancellationToken);

            // 3. 上传文件
            await UploadFileAsync(storeUri, uploadId, uploadHost, audioBytes, auth, crc32, sessionToken, cancellationToken);

            // 4. 检查上传
            await CheckUploadAsync(storeUri, uploadId, uploadHost, crc32, auth, cancellationToken);

            // 5. 提交上传
            await CommitUploadAsync(storeUri, uploadId, uploadHost, audioBytes, auth, sessionToken, cancellationToken);

            return storeUri;
        }

        private async Task<(string accessKey, string secretKey, string sessionToken)> GetUploadSignAsync(
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new { biz = "pc-recognition" });
            var (sign, deviceTime) = await GenerateSignParametersAsync(
                "/lv/v1/upload_sign", "4", "6.6.0", cancellationToken);

            var headers = BuildHeaders(deviceTime, sign);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(UPLOAD_SIGN_URL, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var loginData = data.GetProperty("data");

            return (
                loginData.GetProperty("access_key_id").GetString() ?? string.Empty,
                loginData.GetProperty("secret_access_key").GetString() ?? string.Empty,
                loginData.GetProperty("session_token").GetString() ?? string.Empty
            );
        }

        private async Task<(string storeUri, string auth, string uploadId, string sessionKey, string uploadHost)> GetUploadAuthAsync(
            long fileSize, string accessKey, string secretKey, string sessionToken, CancellationToken cancellationToken)
        {
            var requestParameters = $"Action=ApplyUploadInner&FileSize={fileSize}&FileType=object&IsInner=1&SpaceName=lv-mac-recognition&Version=2020-11-19&s=5y0udbjapi";

            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var datestamp = now.ToString("yyyyMMdd");

            var headers = new Dictionary<string, string>
            {
                ["x-amz-date"] = amzDate,
                ["x-amz-security-token"] = sessionToken
            };

            var signature = GenerateAwsSignature(secretKey, requestParameters, headers, "GET", "", "cn", "vod");
            var authorization = $"AWS4-HMAC-SHA256 Credential={accessKey}/{datestamp}/cn/vod/aws4_request, SignedHeaders=x-amz-date;x-amz-security-token, Signature={signature}";
            headers["authorization"] = authorization;

            var request = new HttpRequestMessage(HttpMethod.Get, $"{VOD_API_URL}?{requestParameters}");
            foreach (var header in headers)
            {
                request.Headers.Add(header.Key, header.Value);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var storeInfo = data.GetProperty("Result").GetProperty("UploadAddress").GetProperty("StoreInfos")[0];

            return (
                storeInfo.GetProperty("StoreUri").GetString() ?? string.Empty,
                storeInfo.GetProperty("Auth").GetString() ?? string.Empty,
                storeInfo.GetProperty("UploadID").GetString() ?? string.Empty,
                data.GetProperty("Result").GetProperty("UploadAddress").GetProperty("SessionKey").GetString() ?? string.Empty,
                data.GetProperty("Result").GetProperty("UploadAddress").GetProperty("UploadHosts")[0].GetString() ?? string.Empty
            );
        }

        private async Task UploadFileAsync(
            string storeUri, string uploadId, string uploadHost, byte[] audioBytes,
            string auth, string crc32, string sessionToken, CancellationToken cancellationToken)
        {
            var url = $"https://{uploadHost}/{storeUri}?partNumber=1&uploadID={uploadId}";
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            request.Headers.Add("Authorization", auth);
            request.Headers.Add("Content-CRC32", crc32);
            request.Content = new ByteArrayContent(audioBytes);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private async Task CheckUploadAsync(
            string storeUri, string uploadId, string uploadHost, string crc32, string auth,
            CancellationToken cancellationToken)
        {
            var url = $"https://{uploadHost}/{storeUri}?uploadID={uploadId}";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            request.Headers.Add("Authorization", auth);
            request.Headers.Add("Content-CRC32", crc32);
            request.Content = new StringContent($"1:{crc32}", Encoding.UTF8);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private async Task CommitUploadAsync(
            string storeUri, string uploadId, string uploadHost, byte[] audioBytes,
            string auth, string sessionToken, CancellationToken cancellationToken)
        {
            var url = $"https://{uploadHost}/{storeUri}?uploadID={uploadId}&partNumber=1&x-amz-security-token={sessionToken}";
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            request.Headers.Add("Authorization", auth);
            request.Content = new ByteArrayContent(audioBytes);

            await _httpClient.SendAsync(request, cancellationToken);
        }

        private async Task<string> SubmitTaskAsync(string storeUri, CancellationToken cancellationToken)
        {
            var payload = new
            {
                adjust_endtime = 200,
                audio = storeUri,
                caption_type = 2,
                client_request_id = "45faf98c-160f-4fae-a649-6d89b0fe35be",
                max_lines = 1,
                songs_info = new[]
                {
                    new { end_time = 6000, id = "", start_time = 0 }
                },
                words_per_line = 16
            };

            var (sign, deviceTime) = await GenerateSignParametersAsync(
                "/lv/v1/audio_subtitle/submit", "4", "6.6.0", cancellationToken);
            var headers = BuildHeaders(deviceTime, sign);

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            foreach (var header in headers)
            {
                content.Headers.Add(header.Key, header.Value);
            }

            var response = await _httpClient.PostAsync(SUBMIT_URL, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (data.GetProperty("ret").GetString() != "0")
            {
                throw new InvalidOperationException($"API 错误: {data.GetProperty("errmsg").GetString()}");
            }

            return data.GetProperty("data").GetProperty("id").GetString() ?? string.Empty;
        }

        private async Task<JsonElement> QueryResultAsync(
            string queryId,
            IProgress<(int progress, string message)>? progress,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 100;
            const int delayMs = 2000;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = new
                {
                    id = queryId,
                    pack_options = new { need_attribute = true }
                };

                var (sign, deviceTime) = await GenerateSignParametersAsync(
                    "/lv/v1/audio_subtitle/query", "4", "6.6.0", cancellationToken);
                var headers = BuildHeaders(deviceTime, sign);

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                foreach (var header in headers)
                {
                    content.Headers.Add(header.Key, header.Value);
                }

                var response = await _httpClient.PostAsync(QUERY_URL, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                if (data.GetProperty("ret").GetString() != "0")
                {
                    throw new InvalidOperationException($"API 错误: {data.GetProperty("errmsg").GetString()}");
                }

                var resultData = data.GetProperty("data");
                if (resultData.TryGetProperty("status", out var status) && status.GetInt32() == 1)
                {
                    return resultData;
                }

                var progressPercent = 60 + (int)((attempt * 1.0 / maxAttempts) * 35);
                progress?.Report((progressPercent, $"等待转录完成... ({attempt + 1}/{maxAttempts})"));

                await Task.Delay(delayMs, cancellationToken);
            }

            throw new TimeoutException("ASR 任务超时");
        }

        private string ConvertToSrt(JsonElement result, bool needWordTimeStamp)
        {
            var srtBuilder = new StringBuilder();
            var utterances = result.GetProperty("utterances").EnumerateArray().ToList();

            if (needWordTimeStamp)
            {
                int index = 1;
                foreach (var utterance in utterances)
                {
                    var words = utterance.GetProperty("words").EnumerateArray();
                    foreach (var word in words)
                    {
                        var text = word.GetProperty("text").GetString()?.Trim() ?? string.Empty;
                        var startTime = word.GetProperty("start_time").GetInt32();
                        var endTime = word.GetProperty("end_time").GetInt32();

                        srtBuilder.AppendLine(index.ToString());
                        srtBuilder.AppendLine($"{FormatTime(startTime)} --> {FormatTime(endTime)}");
                        srtBuilder.AppendLine(text);
                        srtBuilder.AppendLine();
                        index++;
                    }
                }
            }
            else
            {
                int index = 1;
                foreach (var utterance in utterances)
                {
                    var text = utterance.GetProperty("text").GetString()?.Trim() ?? string.Empty;
                    var startTime = utterance.GetProperty("start_time").GetInt32();
                    var endTime = utterance.GetProperty("end_time").GetInt32();

                    srtBuilder.AppendLine(index.ToString());
                    srtBuilder.AppendLine($"{FormatTime(startTime)} --> {FormatTime(endTime)}");
                    srtBuilder.AppendLine(text);
                    srtBuilder.AppendLine();
                    index++;
                }
            }

            return srtBuilder.ToString();
        }

        private string FormatTime(int milliseconds)
        {
            var ts = TimeSpan.FromMilliseconds(milliseconds);
            return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
        }

        private async Task<(string sign, string deviceTime)> GenerateSignParametersAsync(
            string url, string pf, string appvr, CancellationToken cancellationToken)
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var payload = new
            {
                url,
                current_time = currentTime,
                pf,
                appvr,
                tdid = _tdid
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.Add("User-Agent", $"VideoEditor/1.0");
            content.Headers.Add("tdid", _tdid);
            content.Headers.Add("t", currentTime);

            var response = await _httpClient.PostAsync(SIGN_SERVICE_URL, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var sign = data.GetProperty("sign").GetString() ?? string.Empty;

            return (sign.ToLowerInvariant(), currentTime);
        }

        private Dictionary<string, string> BuildHeaders(string deviceTime, string sign)
        {
            return new Dictionary<string, string>
            {
                ["User-Agent"] = "Cronet/TTNetVersion:d4572e53 2024-06-12 QuicVersion:4bf243e0 2023-04-17",
                ["appvr"] = "6.6.0",
                ["device-time"] = deviceTime,
                ["pf"] = "4",
                ["sign"] = sign,
                ["sign-ver"] = "1",
                ["tdid"] = _tdid
            };
        }

        private string GenerateTdid()
        {
            var year = DateTime.Now.Year;
            var i = year.ToString()[3];
            var fr = 390 + int.Parse(i.ToString());
            var ed = (int.Parse(i.ToString()) % 2 != 0) ? "3278516897751" : Environment.MachineName.GetHashCode().ToString("D13");
            return $"{fr}{ed}";
        }

        private string CalculateCrc32(byte[] data)
        {
            using var crc32 = new Crc32();
            var hash = crc32.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private string GenerateAwsSignature(
            string secretKey, string requestParameters, Dictionary<string, string> headers,
            string method, string payload, string region, string service)
        {
            var canonicalUri = "/";
            var canonicalQueryString = requestParameters;
            var canonicalHeaders = string.Join("\n", headers.Select(h => $"{h.Key}:{h.Value}")) + "\n";
            var signedHeaders = string.Join(";", headers.Keys);
            var payloadHash = ComputeSha256(payload);

            var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

            var amzDate = headers["x-amz-date"];
            var datestamp = amzDate.Split('T')[0];

            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{datestamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ComputeSha256(canonicalRequest)}";

            var signingKey = GetSignatureKey(secretKey, datestamp, region, service);
            var signature = ComputeHmacSha256(signingKey, stringToSign);

            return BitConverter.ToString(signature).Replace("-", "").ToLowerInvariant();
        }

        private byte[] GetSignatureKey(string secretKey, string datestamp, string region, string service)
        {
            var kDate = ComputeHmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), datestamp);
            var kRegion = ComputeHmacSha256(kDate, region);
            var kService = ComputeHmacSha256(kRegion, service);
            return ComputeHmacSha256(kService, "aws4_request");
        }

        private byte[] ComputeHmacSha256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private string ComputeSha256(string data)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    // CRC32 计算辅助类
    internal class Crc32 : HashAlgorithm
    {
        private const uint Polynomial = 0xEDB88320;
        private static readonly uint[] Table = new uint[256];
        private uint _crc;

        static Crc32()
        {
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
                }
                Table[i] = crc;
            }
        }

        public Crc32()
        {
            Initialize();
        }

        public override void Initialize()
        {
            _crc = 0xFFFFFFFF;
        }

        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            for (int i = ibStart; i < ibStart + cbSize; i++)
            {
                _crc = (_crc >> 8) ^ Table[array[i] ^ (_crc & 0xFF)];
            }
        }

        protected override byte[] HashFinal()
        {
            _crc ^= 0xFFFFFFFF;
            return BitConverter.GetBytes(_crc);
        }
    }
}

