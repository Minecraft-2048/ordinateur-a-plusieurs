using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

// Dessine les curseurs des joueurs (flèche de couleur + pastille avec le numéro) et l'icône.
namespace OrdinateurAPlusieurs
{
    static class Dessin
    {
        public static readonly Color[] Couleurs = {
            Color.Gray,
            Color.FromArgb(47, 125, 246),  // joueur 1 : bleu
            Color.FromArgb(242, 84, 45),   // joueur 2 : orange
            Color.FromArgb(34, 181, 115),  // joueur 3 : vert
            Color.FromArgb(155, 81, 224),  // joueur 4 : violet
        };
        public static readonly string[] NomsCouleurs = { "", "bleu", "orange", "vert", "violet" };

        // Flèche classique, dans une grille de 12 x 19.
        static readonly PointF[] Fleche = {
            new PointF(0, 0), new PointF(0, 16), new PointF(4, 12.4f), new PointF(6.8f, 18.4f),
            new PointF(9.4f, 17.2f), new PointF(6.6f, 11.4f), new PointF(11.6f, 11.4f)
        };

        /// Marge entre le bord de l'image et la pointe de la flèche (la pointe = le point qui clique).
        public static int Pointe(float taille) { return (int)Math.Ceiling(2 * taille); }

        public static Bitmap Curseur(int joueur, float taille)
        {
            float s = 1.55f * taille;
            int m = Pointe(taille);
            Bitmap b = new Bitmap((int)(17.5f * s) + m, (int)(21f * s) + m, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.TranslateTransform(m, m);
                DessinerFleche(g, Couleurs[joueur], s, joueur);
            }
            return b;
        }

        static void DessinerFleche(Graphics g, Color c, float s, int numero)
        {
            PointF[] p = new PointF[Fleche.Length];
            for (int i = 0; i < p.Length; i++) p[i] = new PointF(Fleche[i].X * s, Fleche[i].Y * s);
            using (GraphicsPath chemin = new GraphicsPath())
            {
                chemin.AddPolygon(p);
                using (Matrix decale = new Matrix())
                using (GraphicsPath ombre = (GraphicsPath)chemin.Clone())
                using (SolidBrush noir = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                {
                    decale.Translate(0.9f * s, 1.1f * s);
                    ombre.Transform(decale);
                    g.FillPath(noir, ombre);
                }
                using (SolidBrush fond = new SolidBrush(c)) g.FillPath(fond, chemin);
                using (Pen sombre = new Pen(Color.FromArgb(200, 20, 20, 30), 2.6f * s / 1.55f)) { sombre.LineJoin = LineJoin.Round; g.DrawPath(sombre, chemin); }
                using (Pen blanc = new Pen(Color.White, 1.3f * s / 1.55f)) { blanc.LineJoin = LineJoin.Round; g.DrawPath(blanc, chemin); }
            }
            if (numero <= 0) return;
            // Pastille avec le numéro du joueur.
            float r = 3.9f * s, cx = 12.9f * s, cy = 15.6f * s;
            RectangleF rond = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (SolidBrush fond = new SolidBrush(c)) g.FillEllipse(fond, rond);
            using (Pen sombre = new Pen(Color.FromArgb(200, 20, 20, 30), 1.6f * s / 1.55f)) g.DrawEllipse(sombre, rond);
            using (Pen blanc = new Pen(Color.White, 1.1f * s / 1.55f)) g.DrawEllipse(blanc, RectangleF.Inflate(rond, -0.9f * s / 1.55f, -0.9f * s / 1.55f));
            using (Font f = new Font("Segoe UI", 4.6f * s, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(numero.ToString(), f, Brushes.White, new RectangleF(rond.X, rond.Y + 0.3f * s, rond.Width, rond.Height), sf);
            }
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
                DessinerFleche(g, Couleurs[1], s, 0);
                g.TranslateTransform(9.5f * s, 3.2f * s);
                DessinerFleche(g, Couleurs[2], s, 0);
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
