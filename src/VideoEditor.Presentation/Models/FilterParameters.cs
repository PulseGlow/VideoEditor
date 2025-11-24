using System;

namespace VideoEditor.Presentation.Models
{
    public enum FilterPreset
    {
        None,
        Retro,
        Monochrome,
        Soft,
        Vibrant,
        Cool,
        Warm,
        Cinema,
        Film,
        Rainbow,
        Mist,
        Sharp,
        Custom
    }

    public class FilterParameters
    {
        public FilterPreset Preset { get; set; } = FilterPreset.None;
        public double Brightness { get; set; } // -100 ~ 100
        public double Contrast { get; set; }   // -100 ~ 100
        public double Saturation { get; set; } // -100 ~ 100
        public double Temperature { get; set; } // -100 ~ 100
        public double Blur { get; set; }       // 0 ~ 20
        public double Sharpen { get; set; }    // 0 ~ 10
        public double Vignette { get; set; }   // 0 ~ 100

        public static FilterParameters CreateDefault()
        {
            return new FilterParameters();
        }

        public FilterParameters Clone()
        {
            return (FilterParameters)MemberwiseClone();
        }

        public bool HasAdjustments()
        {
            return Preset != FilterPreset.None ||
                   Math.Abs(Brightness) > 0.01 ||
                   Math.Abs(Contrast) > 0.01 ||
                   Math.Abs(Saturation) > 0.01 ||
                   Math.Abs(Temperature) > 0.01 ||
                   Math.Abs(Blur) > 0.01 ||
                   Math.Abs(Sharpen) > 0.01 ||
                   Math.Abs(Vignette) > 0.01;
        }
    }
}

