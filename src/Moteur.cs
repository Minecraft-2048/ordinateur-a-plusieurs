using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Le cœur de l'appli.
//
// Windows n'a qu'un seul vrai curseur et mélange toutes les souris dedans. Alors :
//  - « Raw Input » nous dit quelle souris a bougé (chaque souris a sa poignée) ;
//  - un crochet bas niveau (WH_MOUSE_LL) bloque les gestes des vraies souris dès qu'il y a 2 joueurs ;
//  - on renvoie nous-mêmes les gestes du joueur principal (SendInput, marqué MARQUE) : il garde
//    le vrai curseur de Windows, avec sa vitesse et son accélération habituelles ;
//  - les autres joueurs ont un curseur dessiné (une vitre transparente) que l'on déplace nous-mêmes.
//    Quand l'un d'eux clique ou tourne la molette, le vrai curseur « saute » chez lui le temps
//    du geste (un glisser-déposer marche aussi), puis revient chez le joueur principal.
namespace OrdinateurAPlusieurs
{
    sealed class Joueur
    {
        public int Num;
        public string Cle;
        public double X, Y;
        public int Boutons;            // boutons tenus en ce moment (bits)
        public int DerniereActivite;   // Environment.TickCount
        public double DernierEvt;      // ms (chrono), pour la vitesse du geste
        public double DernierDessin;
        public bool EnRetard;
        public Calque Calque;
    }

    sealed class FenetreBrute : NativeWindow
    {
        readonly Moteur moteur;
        public FenetreBrute(Moteur m)
        {
            moteur = m;
            CreateParams cp = new CreateParams();
            cp.Caption = "Ordinateur à plusieurs - souris";
            cp.Style = unchecked((int)0x80000000); // WS_POPUP, jamais affichée
            CreateHandle(cp);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF) moteur.SurBrut(m.LParam);                                  // WM_INPUT
            else if (m.Msg == 0x00FE && m.WParam.ToInt64() == 2) moteur.SurDebranche(m.LParam); // WM_INPUT_DEVICE_CHANGE, retrait
            else if (m.Msg == 0x0312) moteur.BasculerPause();                               // WM_HOTKEY
            base.WndProc(ref m);
        }
    }

    sealed class Moteur
    {
        public const long MARQUE = 0x4D554C54; // « MULT » : nos propres gestes envoyés à Windows
        const int OUBLI_MS = 10 * 60 * 1000;   // une souris immobile 10 min perd son curseur

        static readonly double[] VitessesWindows = { 0, 1 / 32.0, 1 / 16.0, 1 / 8.0, 2 / 8.0, 3 / 8.0, 4 / 8.0, 5 / 8.0, 6 / 8.0, 7 / 8.0, 1.0,
            1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5 };

        readonly Reglages reglages;
        readonly HookProc proc;
        readonly Joueur[] places = new Joueur[5];
        readonly Dictionary<string, Joueur> parCle = new Dictionary<string, Joueur>();
        readonly Dictionary<IntPtr, string> cles = new Dictionary<IntPtr, string>();
        readonly INPUT[] tampon = new INPUT[1];
        readonly int tailleInput = Marshal.SizeOf(typeof(INPUT));
        readonly Stopwatch chrono = Stopwatch.StartNew();
        readonly Bitmap[] images = new Bitmap[5];
        readonly Timer minuteDessin = new Timer(), minuteGarde = new Timer(), minuteRetour = new Timer();
        FenetreBrute fenetre;
        IntPtr crochet, brut;
        Joueur maitre, emprunteur;
        bool actif, pause, secoursDefinitif;
        string secours;
        IntPtr secoursFenetre;
        int secoursDebut, tours;
        float taille;
        double facteurWindows = 1;
        bool precision = true;

        // Compteurs remplis par le crochet (même fil que le reste, pas besoin de verrou).
        bool physEnAttente;
        int dernierPhys, physBloques, physRecus, injEnvoyes, injVus;

        public event Action Change;
        public event Action<string> Message;

        public Moteur(Reglages r)
        {
            reglages = r;
            proc = Crochet;
        }

        public bool EnPause { get { return pause; } }

        // ---------- démarrage / arrêt ----------

        public void Demarrer()
        {
            brut = Marshal.AllocHGlobal(256);
            taille = reglages.Taille * Natif.Echelle();
            LireReglagesWindows();
            fenetre = new FenetreBrute(this);
            RAWINPUTDEVICE[] d = new RAWINPUTDEVICE[1];
            d[0].usUsagePage = 1; d[0].usUsage = 2;      // souris
            d[0].dwFlags = 0x00000100 | 0x00002000;      // RIDEV_INPUTSINK | RIDEV_DEVNOTIFY
            d[0].hwndTarget = fenetre.Handle;
            if (!Natif.RegisterRawInputDevices(d, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                throw new Exception("Windows refuse de donner la liste des souris (RegisterRawInputDevices : " + Marshal.GetLastWin32Error() + ").");
            crochet = Natif.SetWindowsHookEx(14, proc, Natif.GetModuleHandle(null), 0); // WH_MOUSE_LL
            if (crochet == IntPtr.Zero)
                throw new Exception("Impossible d'écouter la souris (SetWindowsHookEx : " + Marshal.GetLastWin32Error() + ").");
            if (!Natif.RegisterHotKey(fenetre.Handle, 1, 0x0002 | 0x0001 | 0x4000, 0x7B)) // Ctrl+Alt+F12, sans répétition
                Journal.Ecrire("Raccourci Ctrl+Alt+F12 déjà pris par un autre programme.");
            minuteDessin.Interval = 15; minuteDessin.Tick += delegate { DessinerEnRetard(); }; minuteDessin.Start();
            minuteGarde.Interval = 250; minuteGarde.Tick += delegate { Garde(); }; minuteGarde.Start();
            minuteRetour.Tick += delegate { minuteRetour.Stop(); if (emprunteur != null && emprunteur.Boutons == 0) Rendre(); };
            Journal.Ecrire("Démarré (échelle " + Natif.Echelle() + ").");
        }

        public void Arreter()
        {
            minuteDessin.Stop(); minuteGarde.Stop(); minuteRetour.Stop();
            Rendre();
            if (crochet != IntPtr.Zero) { Natif.UnhookWindowsHookEx(crochet); crochet = IntPtr.Zero; }
            if (fenetre != null) { Natif.UnregisterHotKey(fenetre.Handle, 1); fenetre.DestroyHandle(); fenetre = null; }
            for (int i = 1; i <= 4; i++) if (places[i] != null && places[i].Calque != null) places[i].Calque.Detruire();
            if (brut != IntPtr.Zero) { Marshal.FreeHGlobal(brut); brut = IntPtr.Zero; }
            Journal.Ecrire("Arrêté.");
        }

        // ---------- crochet : bloque les vraies souris quand il y a plusieurs joueurs ----------

        IntPtr Crochet(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                MSLLHOOKSTRUCT s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                long extra = s.dwExtraInfo.ToInt64();
                if (extra == MARQUE) injVus++;
                // Geste d'une vraie souris (pas envoyé par un programme, pas un doigt ni un stylet)
                else if ((s.flags & 1) == 0 && ((uint)extra & 0xFFFFFF00u) != 0xFF515700u)
                {
                    physEnAttente = true;
                    dernierPhys = Environment.TickCount;
                    if (actif) { physBloques++; return new IntPtr(1); }
                }
            }
            return Natif.CallNextHookEx(crochet, n, w, l);
        }

        // ---------- Raw Input : qui a bougé ? ----------

        public void SurBrut(IntPtr h)
        {
            uint entete = (uint)(8 + 2 * IntPtr.Size), lg = 256;
            uint lu = Natif.GetRawInputData(h, 0x10000003, brut, ref lg, entete); // RID_INPUT
            if (lu == uint.MaxValue || lu < entete + 24) return;
            if (Marshal.ReadInt32(brut) != 0) return; // pas une souris
            IntPtr dev = Marshal.ReadIntPtr(brut, 8);
            int o = (int)entete;
            int drapeaux = (ushort)Marshal.ReadInt16(brut, o);
            int btn = (ushort)Marshal.ReadInt16(brut, o + 4);
            short donnee = Marshal.ReadInt16(brut, o + 6);
            int dx = Marshal.ReadInt32(brut, o + 12), dy = Marshal.ReadInt32(brut, o + 16);
            uint extra = (uint)Marshal.ReadInt32(brut, o + 20);
            if (extra == (uint)MARQUE || (extra & 0xFFFFFF00u) == 0xFF515700u) return; // nous-mêmes, ou un écran tactile

            string cle;
            if (dev == IntPtr.Zero)
            {
                // Sans poignée : soit un programme qui simule la souris (on l'ignore), soit un pavé
                // tactile « de précision », que l'on reconnaît parce que le crochet vient d'en voir passer un geste.
                if (!physEnAttente || Environment.TickCount - dernierPhys > 60) return;
                cle = "PAVE-TACTILE";
            }
            else cle = CleDe(dev);
            physEnAttente = false;
            physRecus++;

            bool absolu = (drapeaux & 1) != 0;
            bool etaitActif = actif;
            Joueur j;
            if (!parCle.TryGetValue(cle, out j))
            {
                if (pause) return;
                bool geste = (btn & 0x0D55) != 0 || absolu || Math.Abs(dx) + Math.Abs(dy) >= 2;
                if (!geste) return;
                j = NouveauJoueur(cle);
                if (j == null) j = maitre; // déjà 4 joueurs : la souris en trop suit le joueur principal
                if (j == null) return;
            }
            j.DerniereActivite = Environment.TickCount;
            double t = chrono.Elapsed.TotalMilliseconds;
            if (!etaitActif) { j.DernierEvt = t; return; } // mode normal : Windows a déjà tout fait

            // 1) mouvement
            if (absolu || dx != 0 || dy != 0)
            {
                if (j == maitre && emprunteur == null)
                {
                    uint f = 0x0001; // MOVE
                    if (absolu) f |= 0x8000 | ((drapeaux & 2) != 0 ? 0x4000u : 0u); // ABSOLUTE (| VIRTUALDESK)
                    Injecter(f, dx, dy, 0);
                }
                else
                {
                    Avancer(j, absolu, (drapeaux & 2) != 0, dx, dy, t);
                    if (j == emprunteur) PoserVraiCurseur(j);
                }
            }
            j.DernierEvt = t;

            // 2) boutons et molettes
            if (btn != 0)
            {
                Bouton(j, btn, 0x0001, 0x0002, 1, 0x0002, 0x0004, 0); // gauche
                Bouton(j, btn, 0x0004, 0x0008, 2, 0x0008, 0x0010, 0); // droit
                Bouton(j, btn, 0x0010, 0x0020, 4, 0x0020, 0x0040, 0); // milieu
                Bouton(j, btn, 0x0040, 0x0080, 8, 0x0080, 0x0100, 1); // côté 1
                Bouton(j, btn, 0x0100, 0x0200, 16, 0x0080, 0x0100, 2); // côté 2
                if ((btn & 0x0400) != 0) Molette(j, 0x0800, donnee);
                if ((btn & 0x0800) != 0) Molette(j, 0x1000, donnee);
            }
        }

        public void SurDebranche(IntPtr dev)
        {
            string cle;
            if (!cles.TryGetValue(dev, out cle)) return;
            cles.Remove(dev);
            if (cles.ContainsValue(cle)) return; // une autre partie de la même souris est encore là
            Joueur j;
            if (parCle.TryGetValue(cle, out j))
            {
                Journal.Ecrire("Souris débranchée : joueur " + j.Num);
                Retirer(j);
            }
        }

        /// Une souris = un appareil. Les souris « gamer » se présentent en plusieurs morceaux
        /// (MI_00, MI_01...) : on remonte jusqu'à l'appareil USB / Bluetooth commun.
        string CleDe(IntPtr dev)
        {
            string cle;
            if (cles.TryGetValue(dev, out cle)) return cle;
            uint lg = 1024;
            StringBuilder sb = new StringBuilder(1024);
            string nom = Natif.GetRawInputDeviceInfo(dev, 0x20000007, sb, ref lg) < 0x7FFFFFFF ? sb.ToString() : ""; // RIDI_DEVICENAME
            cle = nom.Length > 0 ? nom.ToUpperInvariant() : "H" + dev.ToInt64().ToString("X");
            if (nom.Length > 0)
            {
                string id = nom.StartsWith(@"\\?\") ? nom.Substring(4) : nom;
                int g = id.LastIndexOf("#{");
                if (g > 0) id = id.Substring(0, g);
                id = id.Replace('#', '\\');
                cle = id.ToUpperInvariant();
                uint noeud;
                if (Natif.CM_Locate_DevNodeW(out noeud, id, 0) == 0)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        uint parent;
                        if (Natif.CM_Get_Parent(out parent, noeud, 0) != 0) break;
                        StringBuilder p = new StringBuilder(512);
                        if (Natif.CM_Get_Device_IDW(parent, p, p.Capacity, 0) != 0) break;
                        noeud = parent;
                        string pid = p.ToString().ToUpperInvariant();
                        if (pid.IndexOf("&MI_", StringComparison.Ordinal) < 0) { cle = pid; break; }
                    }
                }
            }
            cles[dev] = cle;
            return cle;
        }

        // ---------- joueurs ----------

        Joueur NouveauJoueur(string cle)
        {
            int num = 0;
            for (int i = 1; i <= 4; i++) if (places[i] == null) { num = i; break; }
            if (num == 0) return null;
            Joueur j = new Joueur();
            j.Num = num; j.Cle = cle;
            j.DerniereActivite = Environment.TickCount;
            j.DernierEvt = chrono.Elapsed.TotalMilliseconds;
            POINT p;
            Natif.GetCursorPos(out p);
            if (maitre == null) { maitre = j; j.X = p.X; j.Y = p.Y; }
            else { j.X = p.X + 70; j.Y = p.Y + 50; Borner(j); }
            places[num] = j;
            parCle[cle] = j;
            Journal.Ecrire("Nouvelle souris : joueur " + num + (j == maitre ? " (principal)" : "") + " = " + cle);
            MettreAJourMode();
            if (j != maitre && actif)
                Dire("Joueur " + num + " (" + Dessin.NomsCouleurs[num] + ") : à toi de jouer !");
            Signaler();
            return j;
        }

        void Retirer(Joueur j)
        {
            if (j == emprunteur) Rendre();
            if (j.Calque != null) { j.Calque.Detruire(); j.Calque = null; }
            places[j.Num] = null;
            parCle.Remove(j.Cle);
            if (j == maitre)
            {
                maitre = null;
                for (int i = 1; i <= 4; i++) if (places[i] != null) { maitre = places[i]; break; }
                if (maitre != null) PoserVraiCurseur(maitre);
            }
            MettreAJourMode();
            foreach (Joueur p in Joueurs()) Dessiner(p, true);
            Signaler();
        }

        /// Oublie toutes les souris sauf celle du joueur principal.
        public void Reinitialiser()
        {
            foreach (Joueur j in Joueurs()) if (j != maitre) Retirer(j);
        }

        List<Joueur> Joueurs()
        {
            List<Joueur> l = new List<Joueur>();
            for (int i = 1; i <= 4; i++) if (places[i] != null) l.Add(places[i]);
            return l;
        }

        void MettreAJourMode()
        {
            bool nouveau = !pause && secours == null && Joueurs().Count >= 2;
            if (nouveau == actif) return;
            if (!nouveau) Rendre();
            actif = nouveau;
            if (actif && maitre != null)
            {
                POINT p;
                Natif.GetCursorPos(out p);
                maitre.X = p.X; maitre.Y = p.Y;
            }
            foreach (Joueur j in Joueurs()) Dessiner(j, true);
            Journal.Ecrire(actif ? "Mode plusieurs souris." : "Mode normal" + (pause ? " (pause)." : secours != null ? " (secours : " + secours + ")." : "."));
            Signaler();
        }

        public void BasculerPause()
        {
            pause = !pause;
            secours = null; secoursDefinitif = false;
            MettreAJourMode();
            Dire(pause ? "En pause : toutes les souris bougent le même curseur. Ctrl+Alt+F12 pour reprendre."
                       : "C'est reparti : un curseur par souris.");
            Signaler();
        }

        // ---------- gestes ----------

        void Bouton(Joueur j, int btn, int brutAppui, int brutRelache, int bit, uint fAppui, uint fRelache, uint x)
        {
            if ((btn & brutAppui) != 0) Appui(j, bit, fAppui, x);
            if ((btn & brutRelache) != 0) Relache(j, bit, fRelache, x);
        }

        void Appui(Joueur j, int bit, uint f, uint x)
        {
            if (j == maitre)
            {
                if (emprunteur != null) return; // un autre joueur tient le vrai curseur en ce moment
            }
            else
            {
                if (!PrendreLaMain(j)) return;
                minuteRetour.Stop();
            }
            j.Boutons |= bit;
            Injecter(f, 0, 0, x);
        }

        void Relache(Joueur j, int bit, uint f, uint x)
        {
            if ((j.Boutons & bit) == 0) return; // son appui avait été ignoré
            j.Boutons &= ~bit;
            Injecter(f, 0, 0, x);
            if (j == emprunteur && j.Boutons == 0) ProgrammerRetour(60);
        }

        void Molette(Joueur j, uint f, short delta)
        {
            if (j == maitre)
            {
                if (emprunteur == null) Injecter(f, 0, 0, unchecked((uint)delta));
                return;
            }
            if (!PrendreLaMain(j)) return;
            Injecter(f, 0, 0, unchecked((uint)delta));
            if (j.Boutons == 0) ProgrammerRetour(250);
        }

        /// Le vrai curseur vient chez ce joueur (s'il est libre).
        bool PrendreLaMain(Joueur j)
        {
            if (emprunteur == j) return true;
            if (emprunteur != null) return false;
            if (maitre != null && maitre.Boutons != 0) return false; // le joueur principal tient un bouton
            if (maitre != null)
            {
                POINT p;
                Natif.GetCursorPos(out p);
                maitre.X = p.X; maitre.Y = p.Y;
            }
            emprunteur = j;
            PoserVraiCurseur(j);
            if (maitre != null) Dessiner(maitre, true);
            return true;
        }

        /// Le vrai curseur retourne chez le joueur principal.
        void Rendre()
        {
            minuteRetour.Stop();
            if (emprunteur == null) return;
            Joueur j = emprunteur;
            // Sécurité : on ne laisse jamais un bouton enfoncé.
            if ((j.Boutons & 1) != 0) Injecter(0x0004, 0, 0, 0);
            if ((j.Boutons & 2) != 0) Injecter(0x0010, 0, 0, 0);
            if ((j.Boutons & 4) != 0) Injecter(0x0040, 0, 0, 0);
            if ((j.Boutons & 8) != 0) Injecter(0x0100, 0, 0, 1);
            if ((j.Boutons & 16) != 0) Injecter(0x0100, 0, 0, 2);
            j.Boutons = 0;
            emprunteur = null;
            if (maitre != null)
            {
                PoserVraiCurseur(maitre);
                Dessiner(maitre, true);
            }
        }

        void ProgrammerRetour(int ms)
        {
            minuteRetour.Stop();
            minuteRetour.Interval = ms;
            minuteRetour.Start();
        }

        void PoserVraiCurseur(Joueur j)
        {
            Natif.SetCursorPos((int)Math.Round(j.X), (int)Math.Round(j.Y));
            Injecter(0x0001, 0, 0, 0); // petit « mouvement nul » pour que les fenêtres voient le curseur arriver
        }

        void Injecter(uint f, int dx, int dy, uint donnee)
        {
            tampon[0].type = 0;
            tampon[0].mi.dx = dx; tampon[0].mi.dy = dy;
            tampon[0].mi.mouseData = donnee; tampon[0].mi.dwFlags = f; tampon[0].mi.time = 0;
            tampon[0].mi.dwExtraInfo = new IntPtr(MARQUE);
            Natif.SendInput(1, tampon, tailleInput);
            if (f != 1 || dx != 0 || dy != 0) injEnvoyes++;
        }

        /// Déplace un curseur dessiné, avec une vitesse proche de celle de Windows.
        void Avancer(Joueur j, bool absolu, bool bureau, int dx, int dy, double t)
        {
            if (absolu)
            {
                int vl = 0, vt = 0, vw = Natif.GetSystemMetrics(0), vh = Natif.GetSystemMetrics(1);
                if (bureau) { vl = Natif.GetSystemMetrics(76); vt = Natif.GetSystemMetrics(77); vw = Natif.GetSystemMetrics(78); vh = Natif.GetSystemMetrics(79); }
                j.X = vl + dx * (double)vw / 65536.0;
                j.Y = vt + dy * (double)vh / 65536.0;
            }
            else
            {
                double gain = facteurWindows * reglages.Vitesse;
                if (precision) // « Améliorer la précision du pointeur » : lent = précis, rapide = loin
                {
                    double dt = Math.Max(1.0, Math.Min(25.0, t - j.DernierEvt));
                    double v = Math.Sqrt((double)dx * dx + (double)dy * dy) / dt;
                    gain *= Math.Min(2.6, 0.4 + 0.25 * v);
                }
                j.X += dx * gain;
                j.Y += dy * gain;
            }
            Borner(j);
            Dessiner(j, false);
        }

        /// Garde le curseur sur un écran (même avec plusieurs écrans de tailles différentes).
        void Borner(Joueur j)
        {
            POINT p = new POINT((int)Math.Floor(j.X), (int)Math.Floor(j.Y));
            if (Natif.MonitorFromPoint(p, 0) != IntPtr.Zero) return;
            IntPtr ecran = Natif.MonitorFromPoint(p, 2); // le plus proche
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!Natif.GetMonitorInfo(ecran, ref mi)) return;
            j.X = Math.Max(mi.rcMonitor.Left, Math.Min(mi.rcMonitor.Right - 1, j.X));
            j.Y = Math.Max(mi.rcMonitor.Top, Math.Min(mi.rcMonitor.Bottom - 1, j.Y));
        }

        // ---------- dessin ----------

        Bitmap ImageDe(int num)
        {
            if (images[num] == null) images[num] = Dessin.Curseur(num, taille);
            return images[num];
        }

        void Dessiner(Joueur j, bool force)
        {
            bool visible = actif && (j != maitre || emprunteur != null);
            if (!visible)
            {
                if (j.Calque != null) j.Calque.Cacher();
                j.EnRetard = false;
                return;
            }
            if (j.Calque == null)
            {
                j.Calque = new Calque("joueur " + j.Num);
                j.Calque.Image(ImageDe(j.Num));
            }
            double t = chrono.Elapsed.TotalMilliseconds;
            if (!force && j.Calque.Affiche && t - j.DernierDessin < 7) { j.EnRetard = true; return; }
            j.DernierDessin = t;
            j.EnRetard = false;
            int m = Dessin.Pointe(taille);
            j.Calque.Montrer((int)Math.Round(j.X) - m, (int)Math.Round(j.Y) - m);
        }

        void DessinerEnRetard()
        {
            for (int i = 1; i <= 4; i++) if (places[i] != null && places[i].EnRetard) Dessiner(places[i], true);
        }

        public void ChangerTaille()
        {
            taille = reglages.Taille * Natif.Echelle();
            for (int i = 1; i <= 4; i++)
            {
                if (images[i] != null) { images[i].Dispose(); images[i] = null; }
                if (places[i] != null && places[i].Calque != null) { places[i].Calque.Image(ImageDe(i)); Dessiner(places[i], true); }
            }
        }

        // ---------- surveillance (4 fois par seconde) ----------

        void Garde()
        {
            tours++;
            // Fin du mode secours temporaire.
            if (secours != null && !secoursDefinitif &&
                (Natif.GetForegroundWindow() != secoursFenetre || Environment.TickCount - secoursDebut > 15000))
            {
                secours = null;
                MettreAJourMode();
            }
            if (actif)
            {
                if (physBloques >= 30 && physRecus == 0)
                {
                    // Le crochet bloque une souris que Raw Input ne nous montre pas : on ne la laisse pas morte.
                    Secours("souris non reconnue", true);
                    Dire("Une souris n'est pas reconnue : retour au mode normal. Ctrl+Alt+F12 deux fois pour réessayer.");
                }
                else if (injEnvoyes >= 12 && injVus * 4 < injEnvoyes)
                {
                    // Windows ne laisse pas passer nos gestes : souvent une fenêtre « administrateur » au premier plan.
                    Secours("fenêtre administrateur", false);
                }
            }
            physBloques = physRecus = injEnvoyes = injVus = 0;

            if (tours % 4 == 0) // chaque seconde
            {
                foreach (Joueur j in Joueurs())
                {
                    if (j != maitre && j != emprunteur && Environment.TickCount - j.DerniereActivite > OUBLI_MS)
                    {
                        Journal.Ecrire("Joueur " + j.Num + " oublié (souris immobile).");
                        Retirer(j);
                    }
                    else if (j.Calque != null) j.Calque.Verifier();
                }
            }
            if (tours % 16 == 0) LireReglagesWindows();
        }

        void Secours(string raison, bool definitif)
        {
            secours = raison;
            secoursDefinitif = definitif;
            secoursFenetre = Natif.GetForegroundWindow();
            secoursDebut = Environment.TickCount;
            MettreAJourMode();
        }

        void LireReglagesWindows()
        {
            int v = 10;
            if (Natif.SystemParametersInfo(0x0070, 0, ref v, 0) && v >= 1 && v <= 20) facteurWindows = VitessesWindows[v]; // SPI_GETMOUSESPEED
            int[] souris = new int[3];
            if (Natif.SystemParametersInfo(0x0003, 0, souris, 0)) precision = souris[2] != 0; // SPI_GETMOUSE
        }

        // ---------- état pour le menu ----------

        public string Etat()
        {
            List<Joueur> l = Joueurs();
            if (pause) return "En pause (Ctrl+Alt+F12 pour reprendre)";
            if (secours != null) return "Mode normal : " + secours;
            if (l.Count < 2) return "Une seule souris : bouge une 2e souris !";
            return l.Count + " souris, " + l.Count + " curseurs";
        }

        public List<string> Details()
        {
            List<string> r = new List<string>();
            foreach (Joueur j in Joueurs())
                r.Add("Joueur " + j.Num + " : curseur " + Dessin.NomsCouleurs[j.Num] + (j == maitre ? " (vrai curseur de Windows)" : ""));
            return r;
        }

        void Signaler() { if (Change != null) Change(); }
        void Dire(string s) { if (Message != null) Message(s); }
    }
}
