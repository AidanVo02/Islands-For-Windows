using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace DynamicIsland.Services
{
    public class ExtractedColorPalette
    {
        public Color DominantColor { get; set; }
        public Color SecondaryColor { get; set; }
        public SolidColorBrush DominantBrush { get; set; } = null!;
        public SolidColorBrush SecondaryBrush { get; set; } = null!;
        public LinearGradientBrush EqualizerBrush { get; set; } = null!;
    }

    /// <summary>
    /// Thuật toán Màu Tắc Kè Hoa (Chameleon Adaptive Color Extractor)
    /// Trích xuất màu chủ đạo rực rỡ từ ảnh bìa bài hát và cân chỉnh theo HSL để luôn nổi bật trên nền đen.
    /// </summary>
    public class ColorExtractorService
    {
        private static readonly Lazy<ColorExtractorService> _lazy = new(() => new ColorExtractorService());
        public static ColorExtractorService Instance => _lazy.Value;

        // Màu mặc định khi không tìm thấy màu tươi (Cyan Windows 11 #60CDFF)
        private static readonly Color DefaultDominantColor = Color.FromRgb(0x60, 0xCD, 0xFF);
        private static readonly Color DefaultSecondaryColor = Color.FromRgb(0x8A, 0xDD, 0xFF);

        private readonly ExtractedColorPalette _defaultPalette;
        private readonly Dictionary<int, ExtractedColorPalette> _cache = new();
        private const int MaxCacheEntries = 20;

        private ColorExtractorService()
        {
            _defaultPalette = CreatePalette(DefaultDominantColor, DefaultSecondaryColor);
        }

        public ExtractedColorPalette DefaultPalette => _defaultPalette;

        /// <summary>
        /// Trích xuất màu chủ đạo rực rỡ từ BitmapSource (thời gian xử lý < 1ms nhờ downscale 32x32)
        /// </summary>
        public ExtractedColorPalette ExtractColors(BitmapSource? source)
        {
            if (source == null) return _defaultPalette;

            try
            {
                int cacheKey = source.GetHashCode();
                lock (_cache)
                {
                    if (_cache.TryGetValue(cacheKey, out var cached))
                    {
                        return cached;
                    }
                }

                int width = source.PixelWidth;
                int height = source.PixelHeight;
                if (width <= 0 || height <= 0) return _defaultPalette;

                // Bước 1: Thu nhỏ về kích thước 32x32px bằng TransformedBitmap (< 0.5ms)
                double scaleX = 32.0 / width;
                double scaleY = 32.0 / height;
                var scaleTransform = new ScaleTransform(scaleX, scaleY);
                var transformed = new TransformedBitmap(source, scaleTransform);

                // Chuẩn hóa định dạng Bgra32
                var converted = new FormatConvertedBitmap(transformed, PixelFormats.Bgra32, null, 0);

                const int targetSize = 32;
                int stride = targetSize * 4;
                byte[] pixels = new byte[targetSize * stride];
                converted.CopyPixels(pixels, stride, 0);

                // Bước 2: Lọc điểm ảnh & Phân nhóm Hue (24 buckets, mỗi bucket 15 độ)
                const int numBins = 24;
                const float binSize = 360f / numBins;

                int[] binCounts = new int[numBins];
                float[] binSumH = new float[numBins];
                float[] binSumS = new float[numBins];
                float[] binSumL = new float[numBins];

                int validPixelCount = 0;
                int totalPixels = targetSize * targetSize;

                for (int i = 0; i < totalPixels; i++)
                {
                    int offset = i * 4;
                    byte b = pixels[offset];
                    byte g = pixels[offset + 1];
                    byte r = pixels[offset + 2];
                    byte a = pixels[offset + 3];

                    if (a < 128) continue; // Bỏ qua pixel trong suốt

                    RgbToHsl(r, g, b, out float h, out float s, out float l);

                    // Bộ lọc loại bỏ màu xấu:
                    // Bỏ pixel quá tối (L < 0.15), quá chói (L > 0.90), hoặc quá xám (S < 0.20)
                    if (l < 0.15f || l > 0.90f || s < 0.20f)
                    {
                        continue;
                    }

                    int bin = (int)(h / binSize) % numBins;
                    if (bin < 0) bin += numBins;

                    binCounts[bin]++;
                    binSumH[bin] += h;
                    binSumS[bin] += s;
                    binSumL[bin] += l;
                    validPixelCount++;
                }

                // Nếu ảnh trắng đen hoàn toàn hoặc không có điểm ảnh nào đạt chuẩn
                if (validPixelCount == 0)
                {
                    return _defaultPalette;
                }

                // Tìm nhóm màu có số lượng cao và độ bão hòa (vibrancy) cao nhất
                int bestBin = -1;
                float bestScore = -1f;

                for (int b = 0; b < numBins; b++)
                {
                    if (binCounts[b] == 0) continue;

                    float avgS = binSumS[b] / binCounts[b];
                    float avgL = binSumL[b] / binCounts[b];

                    // Trọng số: Ưu tiên nhóm có nhiều điểm ảnh, Saturation cao và Lightness cân đối
                    float lightnessWeight = 1.0f - MathF.Abs(avgL - 0.50f);
                    float score = binCounts[b] * (0.4f + 0.6f * avgS) * lightnessWeight;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestBin = b;
                    }
                }

                if (bestBin == -1) return _defaultPalette;

                float chosenH = binSumH[bestBin] / binCounts[bestBin];
                float chosenS = binSumS[bestBin] / binCounts[bestBin];
                float chosenL = binSumL[bestBin] / binCounts[bestBin];

                // Bước 3: Cân chỉnh HSL (Perceptual Boost)
                // Ép độ bão hòa lên 75% - 95% để luôn tươi rực rỡ
                float boostedS = Math.Clamp(chosenS, 0.75f, 0.95f);
                // Ép độ sáng nằm trong khung 60% - 72% để luôn nổi bật trên nền đen #000000
                float boostedL = Math.Clamp(chosenL, 0.60f, 0.72f);

                Color dominantColor = HslToRgb(chosenH, boostedS, boostedL);

                // Màu phụ (Secondary Color): Hue + 15 độ, Lightness + 10% cho dải Gradient
                float secH = (chosenH + 15f) % 360f;
                float secS = boostedS;
                float secL = Math.Clamp(boostedL + 0.10f, 0.0f, 0.85f);
                Color secondaryColor = HslToRgb(secH, secS, secL);

                var palette = CreatePalette(dominantColor, secondaryColor);

                lock (_cache)
                {
                    if (_cache.Count >= MaxCacheEntries)
                    {
                        _cache.Clear();
                    }
                    _cache[cacheKey] = palette;
                }

                return palette;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Color extraction error: {ex.Message}");
                return _defaultPalette;
            }
        }

        private static ExtractedColorPalette CreatePalette(Color dominant, Color secondary)
        {
            var domBrush = new SolidColorBrush(dominant);
            domBrush.Freeze();

            var secBrush = new SolidColorBrush(secondary);
            secBrush.Freeze();

            var gradBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1), // Đáy
                EndPoint = new Point(0, 0)    // Đỉnh
            };
            gradBrush.GradientStops.Add(new GradientStop(dominant, 0.0));
            gradBrush.GradientStops.Add(new GradientStop(secondary, 1.0));
            gradBrush.Freeze();

            return new ExtractedColorPalette
            {
                DominantColor = dominant,
                SecondaryColor = secondary,
                DominantBrush = domBrush,
                SecondaryBrush = secBrush,
                EqualizerBrush = gradBrush
            };
        }

        public static void RgbToHsl(byte rByte, byte gByte, byte bByte, out float h, out float s, out float l)
        {
            float r = rByte / 255.0f;
            float g = gByte / 255.0f;
            float b = bByte / 255.0f;

            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            float delta = max - min;

            l = (max + min) / 2.0f;

            if (delta <= 0.0001f)
            {
                h = 0f;
                s = 0f;
            }
            else
            {
                s = l <= 0.5f ? (delta / (max + min)) : (delta / (2.0f - max - min));

                if (MathF.Abs(max - r) < 0.0001f)
                {
                    h = ((g - b) / delta) + (g < b ? 6f : 0f);
                }
                else if (MathF.Abs(max - g) < 0.0001f)
                {
                    h = ((b - r) / delta) + 2f;
                }
                else
                {
                    h = ((r - g) / delta) + 4f;
                }

                h *= 60f;
            }
        }

        public static Color HslToRgb(float h, float s, float l)
        {
            float r, g, b;

            if (s <= 0.0001f)
            {
                r = g = b = l;
            }
            else
            {
                float q = l < 0.5f ? (l * (1.0f + s)) : (l + s - (l * s));
                float p = (2.0f * l) - q;
                float hNorm = h / 360.0f;

                r = HueToRgb(p, q, hNorm + (1.0f / 3.0f));
                g = HueToRgb(p, q, hNorm);
                b = HueToRgb(p, q, hNorm - (1.0f / 3.0f));
            }

            byte rByte = (byte)Math.Clamp((int)MathF.Round(r * 255.0f), 0, 255);
            byte gByte = (byte)Math.Clamp((int)MathF.Round(g * 255.0f), 0, 255);
            byte bByte = (byte)Math.Clamp((int)MathF.Round(b * 255.0f), 0, 255);

            return Color.FromRgb(rByte, gByte, bByte);
        }

        private static float HueToRgb(float p, float q, float t)
        {
            if (t < 0f) t += 1.0f;
            if (t > 1f) t -= 1.0f;
            if (t < 1.0f / 6.0f) return p + ((q - p) * 6.0f * t);
            if (t < 1.0f / 2.0f) return q;
            if (t < 2.0f / 3.0f) return p + ((q - p) * ((2.0f / 3.0f) - t) * 6.0f);
            return p;
        }
    }
}
