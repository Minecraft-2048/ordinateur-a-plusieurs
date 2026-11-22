using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

// Les curseurs des joueurs : la flèche de Windows elle-même (même forme, même taille),
// recolorée, avec une pastille portant le numéro du joueur.
namespace OrdinateurAPlusieurs
{
    sealed class ImageCurseur
    {
        public int L, H, Hx, Hy;
        public uint[] Px;
        public bool[] Inv;

        public bool MemesPixels(ImageCurseur o)
        {
            if (o == null || o.L != L || o.H != H || o.Hx != Hx || o.Hy != Hy) return false;
            for (int i = 0; i < Px.Length; i++) if (Px[i] != o.Px[i] || Inv[i] != o.Inv[i]) return false;
            return true;
        }
    }

    /// Ce qu'il faut pour afficher un joueur : son curseur entier, et la pastille seule
    /// (posée à côté du vrai curseur de Windows quand c'est lui qui l'a).
    sealed class Apparence
    {
        public Bitmap Image, Pastille;
        public int Hx, Hy;           // pointe du curseur dans l'image
        public int PastilleX, PastilleY; // coin de la pastille par rapport à la pointe
    }

    static class Dessin
    {
        public static readonly string[] NomsCouleurs = { "bleu", "orange", "vert", "violet", "rouge", "jaune", "rose", "turquoise", "blanc", "noir" };
        public static readonly Color[] Palette = {
            Color.FromArgb(47, 125, 246), Color.FromArgb(242, 84, 45), Color.FromArgb(34, 181, 115), Color.FromArgb(155, 81, 224),
            Color.FromArgb(225, 35, 55), Color.FromArgb(245, 196, 20), Color.FromArgb(240, 98, 178), Color.FromArgb(18, 186, 196),
            Color.FromArgb(255, 255, 255), Color.FromArgb(45, 45, 50)
        };

        public static string NomCouleur(Color c)
        {
            for (int i = 0; i < Palette.Length; i++) if (Palette[i].ToArgb() == c.ToArgb()) return NomsCouleurs[i];
            return "perso";
        }

        // ---------- la flèche de Windows ----------

        public static ImageCurseur FlecheWindows()
        {
            return Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(32512))); // IDC_ARROW
        }

        /// Lit un curseur en le dessinant sur fond noir puis sur fond blanc : la différence donne la transparence.
        static ImageCurseur Decoder(IntPtr hCurseur)
        {
            if (hCurseur == IntPtr.Zero) return null;
            ICONINFO ii;
            if (!Natif.GetIconInfo(hCurseur, out ii)) return null;
            int w = 0, h = 0;
            try
            {
                BITMAP bm;
                if (ii.hbmColor != IntPtr.Zero && Natif.GetObject(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0) { w = bm.bmWidth; h = bm.bmHeight; }
                else if (ii.hbmMask != IntPtr.Zero && Natif.GetObject(ii.hbmMask, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0) { w = bm.bmWidth; h = bm.bmHeight / 2; }
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) Natif.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) Natif.DeleteObject(ii.hbmMask);
            }
            if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;
            int[] noir = Peindre(hCurseur, w, h, unchecked((int)0xFF000000));
            int[] blanc = Peindre(hCurseur, w, h, -1);
            if (noir == null || blanc == null) return null;
            ImageCurseur img = new ImageCurseur();
            img.L = w; img.H = h; img.Hx = ii.xHotspot; img.Hy = ii.yHotspot;
            img.Px = new uint[w * h]; img.Inv = new bool[w * h];
            for (int i = 0; i < w * h; i++)
            {
                int n = noir[i], b = blanc[i];
                int rn = (n >> 16) & 255, gn = (n >> 8) & 255, bn = n & 255;
                int d = ((((b >> 16) & 255) - rn) + (((b >> 8) & 255) - gn) + ((b & 255) - bn)) / 3;
                if (d < -96) { img.Inv[i] = true; img.Px[i] = 0xFFFFFFFF; continue; }
                int a = 255 - Math.Max(0, Math.Min(255, d));
                if (a < 4) continue;
                int r = Math.Min(255, rn * 255 / a), g = Math.Min(255, gn * 255 / a), bl = Math.Min(255, bn * 255 / a);
                img.Px[i] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)bl;
            }
            return img;
        }

        static int[] Peindre(IntPtr hCurseur, int w, int h, int fond)
        {
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
            IntPtr bits;
            IntPtr dc = Natif.CreateCompatibleDC(IntPtr.Zero);
            IntPtr bmp = Natif.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero) { Natif.DeleteDC(dc); return null; }
            IntPtr ancien = Natif.SelectObject(dc, bmp);
            int[] px = new int[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = fond;
            Marshal.Copy(px, 0, bits, px.Length);
            Natif.DrawIconEx(dc, 0, 0, hCurseur, w, h, 0, IntPtr.Zero, 3); // DI_NORMAL
            Natif.GdiFlush();
            Marshal.Copy(bits, px, 0, px.Length);
            Natif.SelectObject(dc, ancien);
            Natif.DeleteObject(bmp);
            Natif.DeleteDC(dc);
            return px;
        }

        // ---------- fabrication de l'apparence d'un joueur ----------

        public static Apparence Fabriquer(ImageCurseur src, Color c, float taille, int numero)
        {
            Bitmap base_;
            int hx, hy, minX, minY, maxX, maxY;
            if (src != null)
            {
                base_ = Recolorer(src, c, out minX, out minY, out maxX, out maxY);
                hx = src.Hx; hy = src.Hy;
            }
            else
            {
                // Secours : une flèche dessinée à la main, à peu près à la taille de celle de Windows.
                float s = 1.6f * Natif.Echelle();
                base_ = new Bitmap((int)(13 * s) + 4, (int)(20 * s) + 4, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(base_)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TranslateTransform(2, 2); DessinerFleche(g, c, s); }
                hx = 2; hy = 2; minX = 0; minY = 0; maxX = base_.Width - 1; maxY = base_.Height - 1;
            }
            int W = Math.Max(1, (int)Math.Round(base_.Width * taille)), H = Math.Max(1, (int)Math.Round(base_.Height * taille));
            float bw = (maxX - minX + 1) * taille, bh = (maxY - minY + 1) * taille;
            float r = Math.Max(5.5f, bh * 0.23f);
            float cx = minX * taille + bw * 0.93f, cy = minY * taille + bh * 0.87f;
            Apparence a = new Apparence();
            a.Hx = (int)Math.Round(hx * taille); a.Hy = (int)Math.Round(hy * taille);
            a.Image = new Bitmap(Math.Max(W, (int)Math.Ceiling(cx + r + 2)), Math.Max(H, (int)Math.Ceiling(cy + r + 2)), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(a.Image))
            {
                g.InterpolationMode = Math.Abs(taille - 1) < 0.01f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(base_, new Rectangle(0, 0, W, H), 0, 0, base_.Width, base_.Height, GraphicsUnit.Pixel);
                g.PixelOffsetMode = PixelOffsetMode.Default;
                Pastille(g, cx, cy, r, c, numero);
            }
            int cote = (int)Math.Ceiling(2 * r + 4);
            a.Pastille = new Bitmap(cote, cote, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(a.Pastille)) Pastille(g, cote / 2f, cote / 2f, r, c, numero);
            a.PastilleX = (int)Math.Round(cx - cote / 2f) - a.Hx;
            a.PastilleY = (int)Math.Round(cy - cote / 2f) - a.Hy;
            base_.Dispose();
            return a;
        }

        /// Le blanc de la flèche prend la couleur du joueur, le noir reste le contour.
        static Bitmap Recolorer(ImageCurseur src, Color c, out int minX, out int minY, out int maxX, out int maxY)
        {
            int w = src.L, h = src.H;
            minX = w; minY = h; maxX = 0; maxY = 0;
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] ligne = new int[w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        uint p = src.Px[i];
                        int a = (int)(p >> 24);
                        if (src.Inv[i]) a = 255;
                        if (a == 0) { ligne[x] = 0; continue; }
                        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                        double t = 1;
                        if (!src.Inv[i])
                        {
                            int r = (int)(p >> 16) & 255, g = (int)(p >> 8) & 255, bl = (int)p & 255;
                            t = (0.299 * r + 0.587 * g + 0.114 * bl) / 255.0;
                        }
                        int nr = (int)(22 + (c.R - 22) * t), ng = (int)(22 + (c.G - 22) * t), nb = (int)(28 + (c.B - 28) * t);
                        ligne[x] = (a << 24) | (nr << 16) | (ng << 8) | nb;
                    }
                    Marshal.Copy(ligne, 0, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), w);
                }
            }
            finally { b.UnlockBits(d); }
            if (maxX < minX) { minX = 0; minY = 0; maxX = w - 1; maxY = h - 1; }
            return b;
        }

        static void Pastille(Graphics g, float cx, float cy, float r, Color c, int numero)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            RectangleF rond = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (SolidBrush fond = new SolidBrush(c)) g.FillEllipse(fond, rond);
            using (Pen sombre = new Pen(Color.FromArgb(220, 20, 20, 28), Math.Max(1.2f, r * 0.2f))) g.DrawEllipse(sombre, rond);
            bool clair = c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 175;
            using (Font f = new Font("Segoe UI", r * 1.25f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            using (SolidBrush texte = new SolidBrush(clair ? Color.FromArgb(25, 25, 30) : Color.White))
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(numero.ToString(), f, texte, new RectangleF(rond.X, rond.Y + r * 0.08f, rond.Width, rond.Height), sf);
            }
        }

        // Flèche classique dans une grille de 12 x 19 (icône, et secours si la flèche de Windows est illisible).
        static readonly PointF[] Fleche = {
            new PointF(0, 0), new PointF(0, 16), new PointF(4, 12.4f), new PointF(6.8f, 18.4f),
            new PointF(9.4f, 17.2f), new PointF(6.6f, 11.4f), new PointF(11.6f, 11.4f)
        };

        static void DessinerFleche(Graphics g, Color c, float s)
        {
            PointF[] p = new PointF[Fleche.Length];
            for (int i = 0; i < p.Length; i++) p[i] = new PointF(Fleche[i].X * s, Fleche[i].Y * s);
            using (GraphicsPath chemin = new GraphicsPath())
            {
                chemin.AddPolygon(p);
                using (SolidBrush fond = new SolidBrush(c)) g.FillPath(fond, chemin);
                using (Pen sombre = new Pen(Color.FromArgb(220, 20, 20, 28), 1.7f * s / 1.55f)) { sombre.LineJoin = LineJoin.Round; g.DrawPath(sombre, chemin); }
            }
        }

        /// Petit carré de couleur pour les menus.
        public static Bitmap Echantillon(Color c)
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush f = new SolidBrush(c)) g.FillEllipse(f, 1, 1, 13, 13);
                g.DrawEllipse(Pens.DimGray, 1, 1, 13, 13);
            }
            return b;
        }

        /// Image de l'icône : deux curseurs, bleu et orange, côte à côte.
        public static Bitmap Icone(int cote)
        {
            Bitmap b = new Bitmap(cote, cote, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = cote / 24f;
                g.TranslateTransform(1.5f * s, 1.2f * s);
                DessinerFleche(g, Palette[0], s);
                g.TranslateTransform(9.5f * s, 3.2f * s);
                DessinerFleche(g, Palette[1], s);
            }
            return b;
        }

        /// Écrit un fichier .ico (images PNG 16, 32, 48 et 256) pour l'exe.
        public static void EcrireIco(string chemin)
        {
            int[] cotes = { 16, 32, 48, 256 };
            byte[][] png = new byte[cotes.Length][];
            for (int i = 0; i < cotes.Length; i++)
                using (Bitmap b = Icone(cotes[i]))
                using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); png[i] = ms.ToArray(); }
            using (BinaryWriter w = new BinaryWriter(File.Create(chemin)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)cotes.Length);
                int decalage = 6 + 16 * cotes.Length;
                for (int i = 0; i < cotes.Length; i++)
                {
                    w.Write((byte)(cotes[i] >= 256 ? 0 : cotes[i])); w.Write((byte)(cotes[i] >= 256 ? 0 : cotes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(png[i].Length); w.Write(decalage);
                    decalage += png[i].Length;
                }
                foreach (byte[] p in png) w.Write(p);
            }
        }
    }
}
