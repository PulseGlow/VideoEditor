using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// FFmpeg命令帮助服务 - 提供命令查询和帮助功能
    /// </summary>
    public class FFmpegCommandHelpService
    {
        /// <summary>
        /// FFmpeg命令类别
        /// </summary>
        public class CommandCategory
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public List<CommandExample> Examples { get; set; } = new List<CommandExample>();
        }

        /// <summary>
        /// FFmpeg命令示例
        /// </summary>
        public class CommandExample
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Command { get; set; } = "";
            public string Parameters { get; set; } = "";
        }

        private readonly List<CommandCategory> _categories;

        public FFmpegCommandHelpService()
        {
            _categories = InitializeCategories();
        }

        /// <summary>
        /// 初始化命令类别
        /// </summary>
        private List<CommandCategory> InitializeCategories()
        {
            return new List<CommandCategory>
            {
                // 1. 视频剪切
                new CommandCategory
                {
                    Name = "视频剪切",
                    Description = "精确剪切视频片段（使用 -ss 在 -i 之前可提升精度）",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "快速剪切（复制编码）",
                            Description = "快速剪切视频，不重新编码，保持原始质量",
                            Command = "-ss {start} -i \"{input}\" -to {end} -c copy -y \"{output}\"",
                            Parameters = "start: 开始时间 (HH:MM:SS 或 秒数)\nend: 结束时间 (HH:MM:SS 或 秒数)\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "精确剪切（重新编码）",
                            Description = "精确剪切并重新编码，可调整质量",
                            Command = "-ss {start} -i \"{input}\" -to {end} -c:v libx264 -crf 20 -preset faster -c:a aac -b:a 128k -y \"{output}\"",
                            Parameters = "start: 开始时间\nend: 结束时间\ninput: 输入文件路径\noutput: 输出文件路径"
                        }
                    }
                },

                // 2. 视频裁剪
                new CommandCategory
                {
                    Name = "视频裁剪",
                    Description = "裁剪视频画面区域（需要重新编码）",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "基础裁剪",
                            Description = "裁剪指定区域，保持原始分辨率",
                            Command = "-i \"{input}\" -filter:v \"crop={width}:{height}:{x}:{y}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "width: 裁剪宽度\nheight: 裁剪高度\nx: 裁剪起始X坐标\ny: 裁剪起始Y坐标\nquality: CRF质量值 (18-28，越小质量越高)\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "裁剪并调整分辨率",
                            Description = "裁剪后调整到指定分辨率",
                            Command = "-i \"{input}\" -filter:v \"crop={width}:{height}:{x}:{y},scale={out_width}:{out_height}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "width, height, x, y: 裁剪参数\nout_width, out_height: 输出分辨率\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        }
                    }
                },

                // 3. 视频合并
                new CommandCategory
                {
                    Name = "视频合并",
                    Description = "合并多个视频文件",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "使用concat demuxer合并",
                            Description = "快速合并相同编码格式的视频（推荐）",
                            Command = "-f concat -safe 0 -i \"{list_file}\" -c copy -y \"{output}\"",
                            Parameters = "list_file: 包含文件列表的文本文件路径\n格式: file 'path1.mp4'\n      file 'path2.mp4'\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "合并并重新编码",
                            Description = "合并不同格式的视频并统一编码",
                            Command = "-f concat -safe 0 -i \"{list_file}\" -c:v libx264 -crf 20 -preset faster -c:a aac -b:a 128k -y \"{output}\"",
                            Parameters = "list_file: 文件列表路径\noutput: 输出文件路径"
                        }
                    }
                },

                // 4. 视频转码
                new CommandCategory
                {
                    Name = "视频转码",
                    Description = "转换视频编码格式和质量",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "H.264转码",
                            Description = "转换为H.264编码，高质量",
                            Command = "-i \"{input}\" -c:v libx264 -crf {quality} -preset {preset} -tune zerolatency -c:a aac -b:a {audio_bitrate} -movflags +faststart -y \"{output}\"",
                            Parameters = "quality: CRF质量值 (18-28)\npreset: 编码速度 (ultrafast, superfast, veryfast, faster, fast, medium, slow, slower, veryslow)\naudio_bitrate: 音频比特率 (如: 128k, 192k, 256k)\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "H.265转码",
                            Description = "转换为H.265编码，更高压缩率",
                            Command = "-i \"{input}\" -c:v libx265 -crf {quality} -preset {preset} -c:a aac -b:a {audio_bitrate} -movflags +faststart -y \"{output}\"",
                            Parameters = "quality: CRF质量值 (18-28)\npreset: 编码速度\naudio_bitrate: 音频比特率\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "仅复制编码（不重新编码）",
                            Description = "快速转换容器格式，不重新编码",
                            Command = "-i \"{input}\" -c copy -y \"{output}\"",
                            Parameters = "input: 输入文件路径\noutput: 输出文件路径"
                        }
                    }
                },

                // 5. 音频处理
                new CommandCategory
                {
                    Name = "音频处理",
                    Description = "音频提取、调整和转换",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "提取音频",
                            Description = "从视频中提取音频",
                            Command = "-i \"{input}\" -vn -acodec {codec} -b:a {bitrate} -y \"{output}\"",
                            Parameters = "codec: 音频编码器 (aac, libmp3lame, pcm_s16le)\nbitrate: 音频比特率 (如: 128k, 192k, 256k)\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "调整音量",
                            Description = "调整音频音量（0.5=50%, 1.0=100%, 2.0=200%）",
                            Command = "-i \"{input}\" -af \"volume={volume}\" -c:v copy -c:a aac -b:a {bitrate} -y \"{output}\"",
                            Parameters = "volume: 音量倍数 (如: 0.5, 1.0, 1.5, 2.0)\nbitrate: 音频比特率\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "淡入淡出",
                            Description = "添加音频淡入淡出效果",
                            Command = "-i \"{input}\" -af \"afade=t=in:st={fade_in_start}:d={fade_in_duration},afade=t=out:st={fade_out_start}:d={fade_out_duration}\" -c:v copy -c:a aac -b:a {bitrate} -y \"{output}\"",
                            Parameters = "fade_in_start: 淡入开始时间（秒）\nfade_in_duration: 淡入持续时间（秒）\nfade_out_start: 淡出开始时间（秒）\nfade_out_duration: 淡出持续时间（秒）\nbitrate: 音频比特率\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "提取音频（用于ASR）",
                            Description = "提取单声道16kHz PCM音频（用于语音识别）",
                            Command = "-i \"{input}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 -y \"{output}\"",
                            Parameters = "input: 输入文件路径\noutput: 输出音频文件路径"
                        }
                    }
                },

                // 6. 水印处理
                new CommandCategory
                {
                    Name = "水印处理",
                    Description = "添加或移除水印",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "添加图片水印",
                            Description = "在视频上添加图片水印",
                            Command = "-i \"{input}\" -i \"{watermark_image}\" -filter_complex \"overlay={x}:{y}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "watermark_image: 水印图片路径\nx: 水印X坐标\ny: 水印Y坐标\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "添加文字水印",
                            Description = "在视频上添加文字水印",
                            Command = "-i \"{input}\" -vf \"drawtext=text='{text}':fontfile={font}:fontsize={size}:fontcolor={color}:x={x}:y={y}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "text: 水印文字\nfont: 字体文件路径\nsize: 字体大小\ncolor: 字体颜色 (如: white, red, #FFFFFF)\nx, y: 文字位置\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "移除水印（delogo）",
                            Description = "使用delogo滤镜移除指定区域的水印",
                            Command = "-i \"{input}\" -vf \"delogo=x={x}:y={y}:w={width}:h={height}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "x, y: 水印区域左上角坐标\nwidth, height: 水印区域尺寸\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        }
                    }
                },

                // 7. 视频效果
                new CommandCategory
                {
                    Name = "视频效果",
                    Description = "旋转、翻转、缩放等视频效果",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "旋转90度",
                            Description = "顺时针旋转90度",
                            Command = "-i \"{input}\" -vf \"transpose=1\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "transpose值: 1=90度顺时针, 2=90度逆时针, 3=270度顺时针\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "水平翻转",
                            Description = "水平翻转视频",
                            Command = "-i \"{input}\" -vf \"hflip\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "quality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "垂直翻转",
                            Description = "垂直翻转视频",
                            Command = "-i \"{input}\" -vf \"vflip\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "quality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "调整分辨率",
                            Description = "缩放视频到指定分辨率",
                            Command = "-i \"{input}\" -vf \"scale={width}:{height}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "width: 输出宽度\nheight: 输出高度 (可用-1保持比例)\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "调整帧率",
                            Description = "改变视频帧率",
                            Command = "-i \"{input}\" -r {fps} -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "fps: 目标帧率 (如: 24, 30, 60)\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        }
                    }
                },

                // 8. 图片/GIF处理
                new CommandCategory
                {
                    Name = "图片/GIF处理",
                    Description = "提取帧、制作GIF",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "提取单帧",
                            Description = "从视频中提取指定时间的单帧图片",
                            Command = "-ss {time} -i \"{input}\" -vframes 1 -q:v {quality} -y \"{output}\"",
                            Parameters = "time: 时间点 (HH:MM:SS 或 秒数)\nquality: 图片质量 (1-31，越小质量越高)\ninput: 输入文件路径\noutput: 输出图片路径"
                        },
                        new CommandExample
                        {
                            Name = "制作GIF",
                            Description = "将视频片段转换为GIF动画",
                            Command = "-ss {start} -i \"{input}\" -t {duration} -vf \"fps={fps},scale={width}:-1:flags=lanczos\" -y \"{output}\"",
                            Parameters = "start: 开始时间\nduration: 持续时间\nfps: GIF帧率 (如: 10, 15)\nwidth: GIF宽度 (高度自动保持比例)\ninput: 输入文件路径\noutput: 输出GIF路径"
                        },
                        new CommandExample
                        {
                            Name = "提取所有关键帧",
                            Description = "提取视频中的所有关键帧",
                            Command = "-i \"{input}\" -vf \"select='eq(pict_type,I)'\" -vsync vfr -q:v {quality} \"{output_prefix}_%03d.jpg\"",
                            Parameters = "quality: 图片质量\ninput: 输入文件路径\noutput_prefix: 输出文件名前缀"
                        }
                    }
                },

                // 9. 去重处理
                new CommandCategory
                {
                    Name = "去重处理",
                    Description = "视频去重和降噪",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "轻度去重",
                            Description = "轻度去重处理",
                            Command = "-i \"{input}\" -vf \"hqdn3d=4:3:6:4.5\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "quality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "中度去重",
                            Description = "中度去重处理",
                            Command = "-i \"{input}\" -vf \"hqdn3d=6:5:8:6\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "quality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "重度去重",
                            Description = "重度去重处理",
                            Command = "-i \"{input}\" -vf \"hqdn3d=8:7:10:8\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "quality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "色彩调整（亮度/对比度/饱和度）",
                            Description = "调整视频的亮度、对比度和饱和度",
                            Command = "-i \"{input}\" -vf \"eq=brightness={brightness}:contrast={contrast}:saturation={saturation}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "brightness: 亮度调整 (-1.0到1.0，0为原始)\ncontrast: 对比度调整 (-1.0到1.0，0为原始)\nsaturation: 饱和度调整 (0.0到3.0，1.0为原始)\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "添加噪点",
                            Description = "为视频添加噪点效果",
                            Command = "-i \"{input}\" -vf \"noise=alls={intensity}:allf=t+u\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "intensity: 噪点强度 (0-100)\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "添加模糊",
                            Description = "为视频添加模糊效果",
                            Command = "-i \"{input}\" -vf \"boxblur={radius}:{radius}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "radius: 模糊半径 (像素)\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "高斯模糊（移除水印）",
                            Description = "使用高斯模糊移除指定区域（常用于移除水印）",
                            Command = "-i \"{input}\" -filter_complex \"[0:v]crop={width}:{height}:{x}:{y}[crop];[crop]gblur=sigma={sigma}[blur];[0:v][blur]overlay={x}:{y}\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "width, height: 模糊区域尺寸\nx, y: 模糊区域位置\nsigma: 高斯模糊强度 (0.0-1024.0)\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        }
                    }
                },

                // 10. 字幕处理
                new CommandCategory
                {
                    Name = "字幕处理",
                    Description = "添加字幕和字幕文件处理",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "硬编码字幕（SRT）",
                            Description = "将SRT字幕文件硬编码到视频中",
                            Command = "-i \"{input}\" -vf \"subtitles='{subtitle_file}':force_style='FontSize={size},PrimaryColour={color}'\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "subtitle_file: SRT字幕文件路径\nsize: 字体大小\ncolor: 字幕颜色 (如: &Hffffff&)\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "硬编码字幕（ASS）",
                            Description = "将ASS字幕文件硬编码到视频中",
                            Command = "-i \"{input}\" -vf \"ass='{subtitle_file}'\" -c:v libx264 -crf {quality} -preset faster -c:a copy -y \"{output}\"",
                            Parameters = "subtitle_file: ASS字幕文件路径\nquality: CRF质量值\ninput: 输入文件路径\noutput: 输出文件路径"
                        },
                        new CommandExample
                        {
                            Name = "提取字幕",
                            Description = "从视频中提取字幕流",
                            Command = "-i \"{input}\" -map 0:s:0 -c:s copy -y \"{output}\"",
                            Parameters = "input: 输入文件路径\noutput: 输出字幕文件路径 (如: .srt, .ass)"
                        }
                    }
                },

                // 11. 常用参数
                new CommandCategory
                {
                    Name = "常用参数",
                    Description = "FFmpeg常用参数说明",
                    Examples = new List<CommandExample>
                    {
                        new CommandExample
                        {
                            Name = "输入输出",
                            Description = "输入输出文件参数",
                            Command = "-i \"{input}\" \"{output}\"",
                            Parameters = "-i: 指定输入文件\n-y: 自动覆盖输出文件（无需确认）\n-n: 不覆盖已存在的输出文件"
                        },
                        new CommandExample
                        {
                            Name = "视频编码",
                            Description = "视频编码相关参数",
                            Command = "-c:v {codec} -crf {quality} -preset {preset}",
                            Parameters = "-c:v: 视频编码器 (libx264, libx265, copy)\n-crf: 质量值 (18-28，越小质量越高)\n-preset: 编码速度 (ultrafast到veryslow)\n-b:v: 视频比特率 (如: 2M, 5M)\n-tune: 编码调优 (zerolatency, film, animation等)"
                        },
                        new CommandExample
                        {
                            Name = "音频编码",
                            Description = "音频编码相关参数",
                            Command = "-c:a {codec} -b:a {bitrate}",
                            Parameters = "-c:a: 音频编码器 (aac, libmp3lame, copy)\n-b:a: 音频比特率 (如: 128k, 192k, 256k)\n-ar: 采样率 (如: 44100, 48000)\n-ac: 声道数 (1=单声道, 2=立体声)"
                        },
                        new CommandExample
                        {
                            Name = "时间控制",
                            Description = "时间相关参数",
                            Command = "-ss {start} -t {duration} -to {end}",
                            Parameters = "-ss: 开始时间 (在-i前可提升精度)\n-t: 持续时间\n-to: 结束时间"
                        },
                        new CommandExample
                        {
                            Name = "滤镜",
                            Description = "视频和音频滤镜",
                            Command = "-vf \"{filter}\" -af \"{filter}\"",
                            Parameters = "-vf: 视频滤镜\n-af: 音频滤镜\n-filter_complex: 复杂滤镜链（支持多输入多输出）"
                        },
                        new CommandExample
                        {
                            Name = "流映射",
                            Description = "选择和处理特定的流",
                            Command = "-map {stream_spec}",
                            Parameters = "-map: 映射流 (0:v=视频流, 0:a=音频流, 0:s=字幕流)\n示例: -map 0:v -map 0:a:0 (选择第一个视频流和第一个音频流)"
                        },
                        new CommandExample
                        {
                            Name = "元数据",
                            Description = "设置或移除元数据",
                            Command = "-metadata {key}={value}",
                            Parameters = "-metadata: 设置元数据\n示例: -metadata title=\"视频标题\" -metadata author=\"作者\""
                        },
                        new CommandExample
                        {
                            Name = "双通道转码",
                            Description = "使用双通道转码提升质量（需要两次编码）",
                            Command = "第一次: -i \"{input}\" -c:v libx264 -preset slow -b:v {bitrate} -pass 1 -f null /dev/null\n第二次: -i \"{input}\" -c:v libx264 -preset slow -b:v {bitrate} -pass 2 -c:a aac -b:a {audio_bitrate} -y \"{output}\"",
                            Parameters = "bitrate: 目标视频比特率 (如: 2M, 5M)\naudio_bitrate: 音频比特率\n注意: 需要执行两次，第一次生成统计文件，第二次使用统计文件进行编码"
                        }
                    }
                }
            };
        }

        /// <summary>
        /// 获取所有类别
        /// </summary>
        public List<CommandCategory> GetAllCategories()
        {
            return _categories;
        }

        /// <summary>
        /// 根据名称搜索命令
        /// </summary>
        public List<CommandExample> SearchCommands(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return new List<CommandExample>();

            keyword = keyword.ToLower();
            var results = new List<CommandExample>();

            foreach (var category in _categories)
            {
                foreach (var example in category.Examples)
                {
                    if (example.Name.ToLower().Contains(keyword) ||
                        example.Description.ToLower().Contains(keyword) ||
                        example.Command.ToLower().Contains(keyword))
                    {
                        results.Add(example);
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// 根据类别名称获取命令
        /// </summary>
        public CommandCategory? GetCategoryByName(string categoryName)
        {
            return _categories.FirstOrDefault(c => 
                c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
        }
    }
}