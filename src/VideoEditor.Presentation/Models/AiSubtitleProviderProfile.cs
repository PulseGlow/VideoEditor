using System;

namespace VideoEditor.Presentation.Models
{
    public class AiSubtitleProviderProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = "DeepSeek";
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public string EndpointPath { get; set; } = "/v1/audio/transcriptions";
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "whisper-1";
        public string ResponseFormat { get; set; } = "srt";
        public string Notes { get; set; } = string.Empty;

        public AiSubtitleProviderProfile Clone()
        {
            return new AiSubtitleProviderProfile
            {
                Id = Id,
                DisplayName = DisplayName,
                BaseUrl = BaseUrl,
                EndpointPath = EndpointPath,
                ApiKey = ApiKey,
                Model = Model,
                ResponseFormat = ResponseFormat,
                Notes = Notes
            };
        }
    }
}

