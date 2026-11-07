using System;
using System.Globalization;
using System.Text;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// Tiny SVG captcha: four digits drawn as seven-segment paths (no &lt;text&gt;, so the
    /// answer cannot be scraped out of the markup), each rotated a little, with a strike
    /// line and light noise. The answer never leaves the server; a challenge is single-use.
    /// </summary>
    public static class Captcha
    {
        private static readonly object Sync = new object();
        private static readonly Random Rnd = new Random(Environment.TickCount);

        public const int Length = 4;

        // seven-segment masks: bit0=a(top) bit1=b(top-right) bit2=c(bottom-right)
        //                    bit3=d(bottom) bit4=e(bottom-left) bit5=f(top-left) bit6=g(middle)
        private static readonly int[] DigitMask = new int[]
        {
            0x3F, // 0: a b c d e f
            0x06, // 1: b c
            0x5B, // 2: a b d e g
            0x4F, // 3: a b c d g
            0x66, // 4: b c f g
            0x6D, // 5: a c d f g
            0x7D, // 6: a c d e f g
            0x07, // 7: a b c
            0x7F, // 8: all
            0x6F  // 9: a b c d f g
        };

        public static string NewCode()
        {
            StringBuilder sb = new StringBuilder(Length);
            lock (Sync)
            {
                for (int i = 0; i < Length; i++) sb.Append((char)('0' + Rnd.Next(10)));
            }
            return sb.ToString();
        }

        /// <summary>Renders the code as inline SVG (currentColor, fits both themes).</summary>
        public static string Svg(string code)
        {
            if (string.IsNullOrEmpty(code)) code = "0000";

            int width = 132, height = 46;
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(width)
              .Append("\" height=\"").Append(height)
              .Append("\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
              .Append("\" role=\"img\" aria-label=\"captcha\">");

            lock (Sync)
            {
                sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"currentColor\" opacity=\".05\" rx=\"4\"/>");

                for (int i = 0; i < 3; i++)
                {
                    sb.Append("<line x1=\"").Append(Rnd.Next(width)).Append("\" y1=\"").Append(Rnd.Next(height))
                      .Append("\" x2=\"").Append(Rnd.Next(width)).Append("\" y2=\"").Append(Rnd.Next(height))
                      .Append("\" stroke=\"currentColor\" stroke-width=\"1\" opacity=\".15\"/>");
                }

                int step = (width - 16) / Length;
                for (int i = 0; i < code.Length; i++)
                {
                    int digit = code[i] - '0';
                    if (digit < 0 || digit > 9) digit = 0;

                    int cx = 10 + i * step + step / 2;
                    int cy = height / 2 + Rnd.Next(-2, 3);
                    int angle = Rnd.Next(-18, 19);
                    double scale = 1.55 + Rnd.NextDouble() * 0.25;

                    sb.Append("<g transform=\"translate(").Append(cx).Append(' ').Append(cy)
                      .Append(") rotate(").Append(angle).Append(") scale(")
                      .Append(scale.ToString("0.##", CultureInfo.InvariantCulture)).Append(")\"")
                      .Append(" stroke=\"currentColor\" stroke-width=\"1.35\" stroke-linecap=\"round\"")
                      .Append(" fill=\"none\" opacity=\".88\">");

                    AppendDigit(sb, DigitMask[digit]);

                    sb.Append("</g>");

                    // strike-through that makes template matching painful
                    sb.Append("<line x1=\"").Append(cx - 11).Append("\" y1=\"").Append(cy - 2)
                      .Append("\" x2=\"").Append(cx + 11).Append("\" y2=\"").Append(cy + 2)
                      .Append("\" stroke=\"currentColor\" stroke-width=\"1.4\" opacity=\".45\"/>");
                }

                for (int i = 0; i < 10; i++)
                {
                    sb.Append("<circle cx=\"").Append(Rnd.Next(width)).Append("\" cy=\"").Append(Rnd.Next(height))
                      .Append("\" r=\"").Append(Rnd.Next(1, 3)).Append("\" fill=\"currentColor\" opacity=\".2\"/>");
                }
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>Seven-segment digit in a 10x18 box, origin at the centre.</summary>
        private static void AppendDigit(StringBuilder sb, int mask)
        {
            // -5..5 horizontally, -9..9 vertically
            if ((mask & 0x01) != 0) Line(sb, -3.4, -8.4, 3.4, -8.4);   // a
            if ((mask & 0x02) != 0) Line(sb, 3.4, -7.6, 3.4, -1.4);    // b
            if ((mask & 0x04) != 0) Line(sb, 3.4, 1.4, 3.4, 7.6);      // c
            if ((mask & 0x08) != 0) Line(sb, -3.4, 8.4, 3.4, 8.4);     // d
            if ((mask & 0x10) != 0) Line(sb, -3.4, 1.4, -3.4, 7.6);    // e
            if ((mask & 0x20) != 0) Line(sb, -3.4, -7.6, -3.4, -1.4);  // f
            if ((mask & 0x40) != 0) Line(sb, -3.4, 0, 3.4, 0);         // g
        }

        private static void Line(StringBuilder sb, double x1, double y1, double x2, double y2)
        {
            sb.Append("<line x1=\"").Append(N(x1)).Append("\" y1=\"").Append(N(y1))
              .Append("\" x2=\"").Append(N(x2)).Append("\" y2=\"").Append(N(y2)).Append("\"/>");
        }

        private static string N(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static string Normalize(string answer)
        {
            if (answer == null) return "";
            StringBuilder sb = new StringBuilder(answer.Length);
            for (int i = 0; i < answer.Length && sb.Length < 8; i++)
            {
                char c = answer[i];
                if (c >= '0' && c <= '9') sb.Append(c);
            }
            return sb.ToString();
        }

        public static string RandomId()
        {
            byte[] bytes = new byte[9];
            lock (Sync) { Rnd.NextBytes(bytes); }
            StringBuilder sb = new StringBuilder(18);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
