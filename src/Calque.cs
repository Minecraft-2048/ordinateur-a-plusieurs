using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Une « vitre » qui porte le curseur d'un joueur : on la traverse au clic,
// elle reste devant tout et ne prend jamais le clavier.
namespace OrdinateurAPlusieurs
{
    sealed class Calque : NativeWindow
    {
        IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, ancien = IntPtr.Zero, bits = IntPtr.Zero;
        int dibL, dibH, x = -32000, y = -32000;

        public bool Affiche { get; private set; }

        public Calque(string nom)
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "Ordinateur à plusieurs - " + nom;
            cp.Style = unchecked((int)0x80000000); // WS_POPUP
            // LAYERED | TRANSPARENT | TOPMOST | TOOLWINDOW | NOACTIVATE
            cp.ExStyle = 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000;
            cp.X = -32000; cp.Y = -32000; cp.Width = 1; cp.Height = 1;
            CreateHandle(cp);
        }

        /// Change l'image (alpha prémultiplié) sans la déplacer.
        public void Image(Bitmap img)
        {
            int w = img.Width, h = img.Height;
            PreparerDib(w, h);
            BitmapData d = img.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int[] ligne = new int[w];
                for (int yy = 0; yy < h; yy++)
                {
                    Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)yy * d.Stride), ligne, 0, w);
                    Marshal.Copy(ligne, 0, new IntPtr(bits.ToInt64() + (long)yy * w * 4), w);
                }
            }
            finally { img.UnlockBits(d); }
            if (Affiche) Appliquer();
        }

        public void Montrer(int nx, int ny)
        {
            if (dib == IntPtr.Zero) return;
            if (!Affiche) { x = nx; y = ny; Appliquer(); return; }
            if (nx == x && ny == y) return;
            x = nx; y = ny;
            Natif.SetWindowPos(Handle, Natif.HWND_TOPMOST, x, y, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE
        }

        public void Cacher()
        {
            if (!Affiche) return;
            Affiche = false;
            Natif.ShowWindow(Handle, 0); // SW_HIDE
        }

        /// Remet la vitre au premier plan (une autre fenêtre « toujours devant » a pu passer dessus).
        public void Verifier()
        {
            if (!Affiche) return;
            if (Natif.IsIconic(Handle) || !Natif.IsWindowVisible(Handle)) { Appliquer(); return; }
            Natif.SetWindowPos(Handle, Natif.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        void Appliquer()
        {
            POINT pos = new POINT(x, y);
            SIZE taille = new SIZE(dibL, dibH);
            POINT zero = new POINT(0, 0);
            BLENDFUNCTION bf = new BLENDFUNCTION();
            bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1; // AC_SRC_ALPHA
            IntPtr ecran = Natif.GetDC(IntPtr.Zero);
            Natif.UpdateLayeredWindow(Handle, ecran, ref pos, ref taille, dc, ref zero, 0, ref bf, 2); // ULW_ALPHA
            Natif.ReleaseDC(IntPtr.Zero, ecran);
            if (!Affiche || !Natif.IsWindowVisible(Handle))
            {
                Affiche = true;
                Natif.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            }
            Natif.SetWindowPos(Handle, Natif.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        void PreparerDib(int w, int h)
        {
            if (dib != IntPtr.Zero && w == dibL && h == dibH) return;
            LibererDib();
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
            IntPtr ecran = Natif.GetDC(IntPtr.Zero);
            dc = Natif.CreateCompatibleDC(ecran);
            Natif.ReleaseDC(IntPtr.Zero, ecran);
            dib = Natif.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            ancien = Natif.SelectObject(dc, dib);
            dibL = w; dibH = h;
        }

        void LibererDib()
        {
            if (dc != IntPtr.Zero)
            {
                Natif.SelectObject(dc, ancien);
                Natif.DeleteDC(dc);
                dc = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero) { Natif.DeleteObject(dib); dib = IntPtr.Zero; }
        }

        public void Detruire()
        {
            Cacher();
            LibererDib();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; } // WM_NCHITTEST -> HTTRANSPARENT
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }  // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            base.WndProc(ref m);
        }
    }
}
