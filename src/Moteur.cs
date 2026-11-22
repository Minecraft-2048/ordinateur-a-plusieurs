using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Le cœur de l'appli.
//
// Windows n'a qu'un seul vrai curseur et un seul clavier « logique ». Alors :
//  - Raw Input dit quelle souris / quel clavier a envoyé chaque geste ;
//  - des crochets bas niveau (WH_MOUSE_LL, WH_KEYBOARD_LL) bloquent les gestes des vrais appareils
//    dès qu'il y a 2 joueurs, et l'appli renvoie elle-même ce qu'il faut (SendInput, marqué MARQUE) ;
//  - le vrai curseur de Windows appartient au joueur qui bouge (le « porteur ») : il a le survol
//    (croix rouge, boutons qui s'allument) et exactement la vitesse de Windows. Les autres joueurs
//    ont un curseur dessiné ; dès que le porteur s'arrête un instant, ou qu'un autre joueur clique,
//    le vrai curseur passe chez lui ;
//  - chaque clavier est lié à un joueur : il n'écrit qu'à l'endroit choisi par la souris de ce joueur.
//
// Trois fils : un pour les crochets (il ne fait rien d'autre, pour répondre tout de suite à Windows),
// un pour Raw Input et l'envoi des gestes, et le fil de l'interface (icône, dessins des curseurs).
namespace OrdinateurAPlusieurs
{
    sealed class Clavier
    {
        public string Cle;
        public int Num;
        public Joueur Joueur;
    }

    sealed class Joueur
    {
        public int Num;
        public string Cle;
        public double X, Y;
        public double ResteX, ResteY;
        public int Boutons;
        public int DerniereActivite;
        public double DernierEvt, DernierMouvement, DernierBouton;
        public double Agitation;  // mouvements récents (s'efface en ~150 ms) : les petites secousses ne comptent pas
        public IntPtr Fenetre;   // la fenêtre où ce joueur a cliqué ou écrit en dernier
        public Clavier Clavier;
        // Côté interface
        public Calque Calque, Pastille;
        public int VersionDessin = -1;
    }

    /// Une fenêtre invisible qui reçoit les messages d'un fil.
    sealed class FenetreFil : NativeWindow
    {
        public delegate void Traiteur(ref Message m);
        readonly Traiteur traiteur;
        public FenetreFil(string nom, Traiteur t)
        {
            traiteur = t;
            CreateParams cp = new CreateParams();
            cp.Caption = "Ordinateur à plusieurs - " + nom;
            cp.Style = unchecked((int)0x80000000); // WS_POPUP, jamais affichée
            CreateHandle(cp);
        }
        protected override void WndProc(ref Message m)
        {
            traiteur(ref m);
            base.WndProc(ref m);
        }
    }

    sealed class Moteur
    {
        public const long MARQUE = 0x4D554C54; // « MULT » : nos propres gestes envoyés à Windows
        public static bool Essai;              // --essai : les gestes marqués 0x7771 / 0x7772 jouent deux fausses souris
        const int WM_APP_REINSTALLER = 0x8001, WM_APP_FIN = 0x8002;
        const double PASSAGE_MS = 200;          // le porteur doit être immobile ce temps-là pour céder le vrai curseur
        const int OUBLI_MS = 10 * 60 * 1000;    // une souris immobile 10 min perd son curseur

        static readonly double[] VitessesWindows = { 0, 1 / 32.0, 1 / 16.0, 1 / 8.0, 2 / 8.0, 3 / 8.0, 4 / 8.0, 5 / 8.0, 6 / 8.0, 7 / 8.0, 1.0,
            1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5 };

        readonly Reglages reglages;
        readonly object verrou = new object();
        readonly Stopwatch chrono = Stopwatch.StartNew();
        SynchronizationContext ui;

        // ----- fil des crochets -----
        Thread filCrochets;
        FenetreFil fenCrochets;
        HookProc procSouris, procClavier;
        IntPtr crochetSouris, crochetClavier;
        volatile bool bloquerSouris, bloquerClavier, physEnAttente;
        int dernierPhys, crochetPhys, crochetPhysBloques, injVus, clavAttente;
        static readonly int decalageExtraSouris = IntPtr.Size == 8 ? 24 : 20;

        // ----- fil de Raw Input -----
        Thread filEntrees;
        FenetreFil fenEntrees;
        IntPtr brut;
        readonly INPUT[] tampon = new INPUT[1];
        readonly INPUTCLAVIER[] tamponClavier = new INPUTCLAVIER[1];
        readonly int tailleInput = Marshal.SizeOf(typeof(INPUT));
        int brutPhys, injEnvoyes;

        // ----- état partagé (sous verrou) -----
        readonly Joueur[] places = new Joueur[5];
        readonly Dictionary<string, Joueur> parCle = new Dictionary<string, Joueur>();
        readonly Dictionary<IntPtr, string> cles = new Dictionary<IntPtr, string>();
        readonly Dictionary<string, Clavier> claviers = new Dictionary<string, Clavier>();
        List<Joueur> retires = new List<Joueur>();
        Joueur porteur, focus, associerPour;
        bool actif, clavierActif, pause, secoursDefinitif;
        string secours;
        IntPtr secoursFenetre;
        int secoursDebut, tours, evtsSouris, evtsClavier, passages;
        double dernierPassage, maxRetard, maxEnvoi;
        double facteurWindows = 1;
        bool precision = true;

        // ----- fil de l'interface -----
        readonly System.Windows.Forms.Timer minuteGarde = new System.Windows.Forms.Timer();
        volatile ImageCurseur fleche;
        int version;

        // ----- fil du dessin (calé sur l'affichage de l'écran) -----
        Thread filDessin;
        volatile bool finDessin;
        readonly Apparence[] apparences = new Apparence[5];
        int versionVue = -1;

        public event Action Change;
        public event Action<string> Message;

        public Moteur(Reglages r)
        {
            reglages = r;
            procSouris = CrochetSouris;
            procClavier = CrochetClavier;
        }

        // =====================================================================
        // Démarrage / arrêt
        // =====================================================================

        public void Demarrer()
        {
            ui = SynchronizationContext.Current;
            fleche = Dessin.FlecheWindows();
            LireReglagesWindows();
            brut = Marshal.AllocHGlobal(256);

            using (ManualResetEvent pret = new ManualResetEvent(false))
            {
                filCrochets = new Thread(delegate ()
                {
                    fenCrochets = new FenetreFil("crochets", TraiterCrochets);
                    InstallerCrochets();
                    pret.Set();
                    Application.Run();
                    if (crochetSouris != IntPtr.Zero) Natif.UnhookWindowsHookEx(crochetSouris);
                    if (crochetClavier != IntPtr.Zero) Natif.UnhookWindowsHookEx(crochetClavier);
                    fenCrochets.DestroyHandle();
                });
                filCrochets.IsBackground = true;
                filCrochets.Priority = ThreadPriority.Highest;
                filCrochets.SetApartmentState(ApartmentState.STA);
                filCrochets.Start();
                pret.WaitOne(5000);
                if (crochetSouris == IntPtr.Zero) throw new Exception("Impossible d'écouter la souris (SetWindowsHookEx).");
            }

            string erreur = null;
            using (ManualResetEvent pret = new ManualResetEvent(false))
            {
                filEntrees = new Thread(delegate ()
                {
                    fenEntrees = new FenetreFil("entrées", TraiterEntrees);
                    RAWINPUTDEVICE[] d = new RAWINPUTDEVICE[2];
                    d[0].usUsagePage = 1; d[0].usUsage = 2; d[0].dwFlags = 0x00000100 | 0x00002000; d[0].hwndTarget = fenEntrees.Handle; // souris
                    d[1].usUsagePage = 1; d[1].usUsage = 6; d[1].dwFlags = 0x00000100 | 0x00002000; d[1].hwndTarget = fenEntrees.Handle; // claviers
                    if (!Natif.RegisterRawInputDevices(d, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                        erreur = "Windows refuse de donner la liste des souris et des claviers (" + Marshal.GetLastWin32Error() + ").";
                    if (!Natif.RegisterHotKey(fenEntrees.Handle, 1, 0x0002 | 0x0001 | 0x4000, 0x7B)) // Ctrl+Alt+F12
                        Journal.Ecrire("Raccourci Ctrl+Alt+F12 déjà pris par un autre programme.");
                    pret.Set();
                    if (erreur != null) return;
                    Application.Run();
                    Natif.UnregisterHotKey(fenEntrees.Handle, 1);
                    fenEntrees.DestroyHandle();
                });
                filEntrees.IsBackground = true;
                filEntrees.Priority = ThreadPriority.AboveNormal;
                filEntrees.SetApartmentState(ApartmentState.STA);
                filEntrees.Start();
                pret.WaitOne(5000);
            }
            if (erreur != null) throw new Exception(erreur);

            filDessin = new Thread(BoucleDessin);
            filDessin.IsBackground = true;
            filDessin.Priority = ThreadPriority.AboveNormal;
            filDessin.SetApartmentState(ApartmentState.STA);
            filDessin.Start();
            minuteGarde.Interval = 250; minuteGarde.Tick += delegate { Garde(); }; minuteGarde.Start();
            Journal.Ecrire("Démarré, version " + Programme.Version + ", échelle " + Natif.Echelle() + ", flèche Windows " +
                (fleche != null ? fleche.L + "x" + fleche.H : "illisible") + ".");
        }

        public void Arreter()
        {
            minuteGarde.Stop();
            lock (verrou)
            {
                bloquerSouris = bloquerClavier = false;
                if (porteur != null) RelacherTout(porteur);
            }
            if (fenEntrees != null) Natif.PostMessage(fenEntrees.Handle, WM_APP_FIN, IntPtr.Zero, IntPtr.Zero);
            if (fenCrochets != null) Natif.PostMessage(fenCrochets.Handle, WM_APP_FIN, IntPtr.Zero, IntPtr.Zero);
            if (filEntrees != null) filEntrees.Join(2000);
            if (filCrochets != null) filCrochets.Join(2000);
            finDessin = true;
            if (filDessin != null) filDessin.Join(2000);
            Journal.Ecrire("Arrêté.");
        }

        // =====================================================================
        // Fil des crochets : décider vite, bloquer ou laisser passer
        // =====================================================================

        void TraiterCrochets(ref Message m)
        {
            if (m.Msg == WM_APP_REINSTALLER) InstallerCrochets();
            else if (m.Msg == WM_APP_FIN) Application.ExitThread();
        }

        void InstallerCrochets()
        {
            if (crochetSouris != IntPtr.Zero) Natif.UnhookWindowsHookEx(crochetSouris);
            if (crochetClavier != IntPtr.Zero) Natif.UnhookWindowsHookEx(crochetClavier);
            IntPtr mod = Natif.GetModuleHandle(null);
            crochetSouris = Natif.SetWindowsHookEx(14, procSouris, mod, 0);   // WH_MOUSE_LL
            crochetClavier = Natif.SetWindowsHookEx(13, procClavier, mod, 0); // WH_KEYBOARD_LL
        }

        IntPtr CrochetSouris(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                uint flags = (uint)Marshal.ReadInt32(l, 12);
                long extra = Marshal.ReadIntPtr(l, decalageExtraSouris).ToInt64();
                if (extra == MARQUE) Interlocked.Increment(ref injVus);
                // Geste d'une vraie souris (pas envoyé par un programme, pas un doigt ni un stylet)
                else if (((flags & 1) == 0 && ((uint)extra & 0xFFFFFF00u) != 0xFF515700u) || (Essai && (extra == 0x7771 || extra == 0x7772)))
                {
                    Interlocked.Increment(ref crochetPhys);
                    dernierPhys = Environment.TickCount;
                    physEnAttente = true;
                    if (bloquerSouris) { Interlocked.Increment(ref crochetPhysBloques); return new IntPtr(1); }
                }
            }
            return Natif.CallNextHookEx(IntPtr.Zero, n, w, l);
        }

        IntPtr CrochetClavier(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                uint vk = (uint)Marshal.ReadInt32(l, 0), scan = (uint)Marshal.ReadInt32(l, 4), flags = (uint)Marshal.ReadInt32(l, 8);
                long extra = Marshal.ReadIntPtr(l, 16).ToInt64();
                if (extra == MARQUE) Interlocked.Increment(ref injVus);
                // Vraie touche ; on laisse passer le faux Ctrl d'AltGr (0x21D) et les touches multimédia (volume...).
                else if ((flags & 0x10) == 0 && (scan & 0x200) == 0 && !(vk >= 0xA6 && vk <= 0xB7) && vk != 0x5F)
                {
                    if (bloquerClavier)
                    {
                        if (clavAttente == 0) clavAttente = Environment.TickCount | 1;
                        return new IntPtr(1);
                    }
                }
            }
            return Natif.CallNextHookEx(IntPtr.Zero, n, w, l);
        }

        // =====================================================================
        // Fil de Raw Input : qui a fait quoi, et que faut-il renvoyer à Windows
        // =====================================================================

        void TraiterEntrees(ref Message m)
        {
            if (m.Msg == 0x00FF) SurBrut(m.LParam);                                           // WM_INPUT
            else if (m.Msg == 0x00FE && m.WParam.ToInt64() == 2) SurDebranche(m.LParam);      // WM_INPUT_DEVICE_CHANGE (retrait)
            else if (m.Msg == 0x0312) BasculerPause();                                        // WM_HOTKEY
            else if (m.Msg == WM_APP_FIN) Application.ExitThread();
        }

        void SurBrut(IntPtr h)
        {
            uint entete = (uint)(8 + 2 * IntPtr.Size), lg = 256;
            uint lu = Natif.GetRawInputData(h, 0x10000003, brut, ref lg, entete); // RID_INPUT
            if (lu == uint.MaxValue || lu < entete + 16) return;
            int type = Marshal.ReadInt32(brut);
            IntPtr dev = Marshal.ReadIntPtr(brut, 8);
            int retard = Environment.TickCount - Natif.GetMessageTime();
            lock (verrou)
            {
                if (retard > maxRetard && retard < 60000) maxRetard = retard;
                if (type == 0 && lu >= entete + 24) SourisBrute(dev, (int)entete);
                else if (type == 1) ClavierBrut(dev, (int)entete);
            }
        }

        // ---------------- souris ----------------

        void SourisBrute(IntPtr dev, int o)
        {
            int drapeaux = (ushort)Marshal.ReadInt16(brut, o);
            int btn = (ushort)Marshal.ReadInt16(brut, o + 4);
            short donnee = Marshal.ReadInt16(brut, o + 6);
            int dx = Marshal.ReadInt32(brut, o + 12), dy = Marshal.ReadInt32(brut, o + 16);
            uint extra = (uint)Marshal.ReadInt32(brut, o + 20);
            if (extra == (uint)MARQUE || (extra & 0xFFFFFF00u) == 0xFF515700u) return; // nous-mêmes, ou un écran tactile

            string cle;
            if (dev == IntPtr.Zero && Essai && (extra == 0x7771 || extra == 0x7772)) cle = "ESSAI-" + (extra & 0xF);
            else if (dev == IntPtr.Zero)
            {
                // Sans poignée : un programme qui simule la souris (ignoré), ou un pavé tactile « de précision »,
                // reconnu parce que le crochet vient d'en voir passer un vrai geste.
                if (!physEnAttente || Environment.TickCount - dernierPhys > 60) return;
                cle = "PAVE-TACTILE";
            }
            else cle = CleDe(dev);
            physEnAttente = false;
            Interlocked.Increment(ref brutPhys);
            evtsSouris++;

            bool absolu = (drapeaux & 1) != 0, bureau = (drapeaux & 2) != 0;
            bool etaitActif = actif;
            Joueur j;
            if (!parCle.TryGetValue(cle, out j))
            {
                if (pause) return;
                bool geste = (btn & 0x0D55) != 0 || absolu || Math.Abs(dx) + Math.Abs(dy) >= 2;
                if (!geste) return;
                j = NouveauJoueur(cle);
                if (j == null) j = porteur != null ? porteur : Premier(); // déjà 4 joueurs : la souris en trop suit le porteur
                if (j == null) return;
            }
            j.DerniereActivite = Environment.TickCount;
            double t = chrono.Elapsed.TotalMilliseconds;
            bool bouge = absolu || dx != 0 || dy != 0;
            if (!etaitActif)
            {
                // Mode normal : Windows a déjà tout fait.
                if (bouge) Agiter(j, Math.Abs(dx) + Math.Abs(dy), t);
                j.DernierEvt = t;
                if ((btn & 0x0155) != 0) j.DernierBouton = t;
                return;
            }

            // 1) mouvement
            if (bouge)
            {
                Agiter(j, Math.Abs(dx) + Math.Abs(dy), t);
                if (j == porteur) DeplacerVrai(j, absolu, bureau, dx, dy);
                else
                {
                    Avancer(j, absolu, bureau, dx, dy, t);
                    // Le vrai curseur ne change de main que pour un vrai geste (pas un tremblement), quand le porteur
                    // ne s'en sert plus, et pas plus d'une fois tous les 300 ms (sinon il sauterait sans arrêt entre deux joueurs).
                    if (porteur == null || (porteur.Boutons == 0 && fileClics.Count == 0 && Agite(j, t) > SEUIL_GESTE
                        && Agite(porteur, t) < SEUIL_OCCUPE && t - porteur.DernierBouton > PASSAGE_MS && t - dernierPassage > 300))
                        Prendre(j);
                }
            }
            j.DernierEvt = t;

            // 2) boutons et molettes
            if (btn != 0)
            {
                Bouton(j, btn, 0x0001, 0x0002, 1, 0x0002, 0x0004, 0, t); // gauche
                Bouton(j, btn, 0x0004, 0x0008, 2, 0x0008, 0x0010, 0, t); // droit
                Bouton(j, btn, 0x0010, 0x0020, 4, 0x0020, 0x0040, 0, t); // milieu
                Bouton(j, btn, 0x0040, 0x0080, 8, 0x0080, 0x0100, 1, t); // côté 1
                Bouton(j, btn, 0x0100, 0x0200, 16, 0x0080, 0x0100, 2, t); // côté 2
                if ((btn & 0x0400) != 0) Molette(j, 0x0800, donnee, t);
                if ((btn & 0x0800) != 0) Molette(j, 0x1000, donnee, t);
            }
        }

        const double SEUIL_OCCUPE = 15, SEUIL_GESTE = 20;

        void Agiter(Joueur j, int quantite, double t)
        {
            j.Agitation = Agite(j, t) + quantite;
            j.DernierMouvement = t;
        }

        /// Quantité de mouvement récente (s'efface en ~150 ms) : une main simplement posée sur la souris reste sous 15.
        static double Agite(Joueur p, double t)
        {
            return p.Agitation * Math.Exp(-(t - p.DernierMouvement) / 150.0);
        }

        /// Un clic qui attend son tour : le vrai curseur est occupé par le clic (ou le glisser-déposer) d'un autre joueur.
        sealed class Clic
        {
            public Joueur J;
            public int Bit;
            public uint FAppui, FRelache, X;
            public double PX, PY;
            public bool Relache;
        }

        readonly List<Clic> fileClics = new List<Clic>();

        void Bouton(Joueur j, int btn, int brutAppui, int brutRelache, int bit, uint fAppui, uint fRelache, uint x, double t)
        {
            if ((btn & brutAppui) != 0)
            {
                j.DernierBouton = t;
                if (j == porteur || porteur == null || (porteur.Boutons == 0 && fileClics.Count == 0))
                {
                    Prendre(j);
                    j.Boutons |= bit;
                    ChoisirEndroit(j);
                    Injecter(fAppui, 0, 0, x);
                }
                else
                {
                    // Deux joueurs cliquent en même temps : ce clic passera dès que l'autre aura relâché.
                    Clic c = new Clic();
                    c.J = j; c.Bit = bit; c.FAppui = fAppui; c.FRelache = fRelache; c.X = x; c.PX = j.X; c.PY = j.Y;
                    fileClics.Add(c);
                }
            }
            if ((btn & brutRelache) != 0)
            {
                j.DernierBouton = t;
                foreach (Clic c in fileClics)
                    if (c.J == j && c.Bit == bit && !c.Relache) { c.Relache = true; return; }
                if (j != porteur) return;
                j.Boutons &= ~bit;
                Injecter(fRelache, 0, 0, x); // toujours renvoyé : un relâchement en trop ne fait aucun mal
                if (j.Boutons == 0) TraiterFile();
            }
        }

        /// Fait passer les clics en attente, l'un après l'autre, chacun à l'endroit où il a été fait.
        void TraiterFile()
        {
            while (fileClics.Count > 0 && (porteur == null || porteur.Boutons == 0))
            {
                Clic c = fileClics[0];
                fileClics.RemoveAt(0);
                if (places[c.J.Num] != c.J) continue; // joueur parti entre-temps
                double vx = c.J.X, vy = c.J.Y;
                c.J.X = c.PX; c.J.Y = c.PY;
                if (porteur == c.J) { Natif.SetCursorPos((int)Math.Round(c.PX), (int)Math.Round(c.PY)); Injecter(0x0001, 0, 0, 0); }
                else Prendre(c.J);
                ChoisirEndroit(c.J);
                Injecter(c.FAppui, 0, 0, c.X);
                if (c.Relache)
                {
                    Injecter(c.FRelache, 0, 0, c.X);
                    c.J.X = vx; c.J.Y = vy; // puis le curseur rejoint l'endroit où ce joueur est allé depuis
                    if (Math.Abs(vx - c.PX) >= 1 || Math.Abs(vy - c.PY) >= 1) { Natif.SetCursorPos((int)Math.Round(vx), (int)Math.Round(vy)); Injecter(0x0001, 0, 0, 0); }
                }
                else c.J.Boutons |= c.Bit; // il tient encore le bouton : glisser-déposer
            }
        }

        void Molette(Joueur j, uint f, short delta, double t)
        {
            if (j != porteur)
            {
                if (porteur != null && (porteur.Boutons != 0 || fileClics.Count > 0)) return;
                Prendre(j);
            }
            j.DernierBouton = t;
            Injecter(f, 0, 0, unchecked((uint)delta));
        }

        /// Le vrai curseur de Windows passe chez ce joueur.
        void Prendre(Joueur j)
        {
            if (porteur == j) return;
            Joueur ancien = porteur;
            if (ancien != null)
            {
                POINT p;
                Natif.GetCursorPos(out p);
                ancien.X = p.X; ancien.Y = p.Y;
                ancien.ResteX = ancien.ResteY = 0;
                if (ancien.Boutons != 0) RelacherTout(ancien);
            }
            porteur = j;
            dernierPassage = chrono.Elapsed.TotalMilliseconds;
            passages++;
            j.ResteX = j.ResteY = 0;
            Natif.SetCursorPos((int)Math.Round(j.X), (int)Math.Round(j.Y));
            Injecter(0x0001, 0, 0, 0); // « mouvement nul » : les fenêtres voient le curseur arriver (survol)
        }

        /// Le porteur bouge : on renvoie son geste tel quel, Windows applique sa vitesse et son accélération.
        void DeplacerVrai(Joueur j, bool absolu, bool bureau, int dx, int dy)
        {
            if (absolu) { Injecter(0x0001 | 0x8000 | (bureau ? 0x4000u : 0u), dx, dy, 0); return; }
            double v = reglages.VitesseDe(j.Num);
            if (Math.Abs(v - 1) < 0.01) { Injecter(0x0001, dx, dy, 0); return; }
            j.ResteX += dx * v; j.ResteY += dy * v;
            int ix = (int)j.ResteX, iy = (int)j.ResteY;
            j.ResteX -= ix; j.ResteY -= iy;
            if (ix != 0 || iy != 0) Injecter(0x0001, ix, iy, 0);
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
                double gain = facteurWindows * reglages.VitesseDe(j.Num);
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

        void RelacherTout(Joueur j)
        {
            if ((j.Boutons & 1) != 0) Injecter(0x0004, 0, 0, 0);
            if ((j.Boutons & 2) != 0) Injecter(0x0010, 0, 0, 0);
            if ((j.Boutons & 4) != 0) Injecter(0x0040, 0, 0, 0);
            if ((j.Boutons & 8) != 0) Injecter(0x0100, 0, 0, 1);
            if ((j.Boutons & 16) != 0) Injecter(0x0100, 0, 0, 2);
            j.Boutons = 0;
        }

        void Injecter(uint f, int dx, int dy, uint donnee)
        {
            tampon[0].type = 0;
            tampon[0].mi.dx = dx; tampon[0].mi.dy = dy;
            tampon[0].mi.mouseData = donnee; tampon[0].mi.dwFlags = f; tampon[0].mi.time = 0;
            tampon[0].mi.dwExtraInfo = new IntPtr(MARQUE);
            long debut = Stopwatch.GetTimestamp();
            Natif.SendInput(1, tampon, tailleInput);
            double duree = (Stopwatch.GetTimestamp() - debut) * 1000.0 / Stopwatch.Frequency;
            if (duree > maxEnvoi) maxEnvoi = duree;
            if (f != 1 || dx != 0 || dy != 0) Interlocked.Increment(ref injEnvoyes);
        }

        // ---------------- claviers ----------------

        static IntPtr Racine(IntPtr h) { return h == IntPtr.Zero ? h : Natif.GetAncestor(h, 2); } // GA_ROOT

        /// Un clic choisit l'endroit où écrira le clavier de ce joueur.
        void ChoisirEndroit(Joueur j)
        {
            focus = j;
            POINT p = new POINT((int)Math.Round(j.X), (int)Math.Round(j.Y));
            Natif.GetCursorPos(out p);
            j.Fenetre = Racine(Natif.WindowFromPoint(p));
        }

        void ClavierBrut(IntPtr dev, int o)
        {
            int make = (ushort)Marshal.ReadInt16(brut, o), fl = (ushort)Marshal.ReadInt16(brut, o + 2), vk = (ushort)Marshal.ReadInt16(brut, o + 6);
            uint extra = (uint)Marshal.ReadInt32(brut, o + 12);
            if (extra == (uint)MARQUE || dev == IntPtr.Zero) return; // nous-mêmes, ou un programme qui simule le clavier
            clavAttente = 0;
            if (vk == 0xFF || (vk >= 0xA6 && vk <= 0xB7) || vk == 0x5F) return; // touche fantôme, touches multimédia
            evtsClavier++;
            bool etaitRoute = clavierActif;
            string cle = CleDe(dev);
            Clavier c;
            if (!claviers.TryGetValue(cle, out c))
            {
                c = new Clavier();
                c.Cle = cle;
                for (int n = 1; ; n++) { bool pris = false; foreach (Clavier k in claviers.Values) if (k.Num == n) pris = true; if (!pris) { c.Num = n; break; } }
                claviers[cle] = c;
                Journal.Ecrire("Nouveau clavier n°" + c.Num + " = " + cle);
                MettreAJourMode();
            }
            bool appui = (fl & 1) == 0;
            if (appui && c.Joueur == null && associerPour == null)
            {
                Joueur p = ChoisirPourClavier();
                if (p != null) Associer(c, p);
            }
            if (!etaitRoute) return; // un seul clavier : Windows a déjà tout fait

            if (!appui) { InjecterTouche(make, fl, vk, true); return; } // relâchement : toujours renvoyé
            if (associerPour != null)
            {
                Joueur p = associerPour;
                associerPour = null;
                Associer(c, p);
                Dire("Clavier n°" + c.Num + " associé au joueur " + p.Num + " (" + Dessin.NomCouleur(reglages.CouleurDe(p.Num)) + ").");
                return; // la touche servait juste à choisir
            }
            Joueur j = c.Joueur;
            if (j != null)
            {
                IntPtr premier = Racine(Natif.GetForegroundWindow());
                if (j == focus) j.Fenetre = premier; // suit Alt+Tab et les fenêtres qui s'ouvrent
                else if (focus == null || focus.Clavier == null || places[focus.Num] != focus)
                {
                    focus = j; j.Fenetre = premier; // l'endroit n'appartient à aucun autre clavier
                }
                else if (j.Fenetre != IntPtr.Zero && j.Fenetre != premier && Natif.IsWindow(j.Fenetre))
                {
                    Activer(j.Fenetre); // retourne à la fenêtre de ce joueur
                    focus = j;
                }
                else return; // l'endroit choisi appartient à un autre joueur : ce clavier n'y écrit pas
            }
            InjecterTouche(make, fl, vk, false);
        }

        Joueur ChoisirPourClavier()
        {
            if (focus != null && focus.Clavier == null && places[focus.Num] == focus) return focus;
            if (porteur != null && porteur.Clavier == null && places[porteur.Num] == porteur) return porteur;
            for (int i = 1; i <= 4; i++) if (places[i] != null && places[i].Clavier == null) return places[i];
            return null;
        }

        void Associer(Clavier c, Joueur j)
        {
            if (j.Clavier != null && j.Clavier != c) j.Clavier.Joueur = null;
            if (c.Joueur != null && c.Joueur != j) c.Joueur.Clavier = null;
            c.Joueur = j;
            j.Clavier = c;
            Journal.Ecrire("Clavier n°" + c.Num + " -> joueur " + j.Num);
            Signaler();
        }

        void Activer(IntPtr h)
        {
            if (Natif.IsIconic(h)) Natif.ShowWindow(h, 9); // SW_RESTORE
            // Un geste vide envoyé juste avant autorise SetForegroundWindow.
            tampon[0].type = 0; tampon[0].mi.dx = 0; tampon[0].mi.dy = 0; tampon[0].mi.mouseData = 0; tampon[0].mi.dwFlags = 0;
            tampon[0].mi.dwExtraInfo = new IntPtr(MARQUE);
            Natif.SendInput(1, tampon, tailleInput);
            if (Natif.SetForegroundWindow(h)) return;
            uint autre = Natif.GetWindowThreadProcessId(Natif.GetForegroundWindow(), IntPtr.Zero), moi = Natif.GetCurrentThreadId();
            if (autre == 0 || autre == moi) return;
            Natif.AttachThreadInput(moi, autre, true);
            Natif.SetForegroundWindow(h);
            Natif.AttachThreadInput(moi, autre, false);
        }

        void InjecterTouche(int make, int fl, int vk, bool relache)
        {
            uint f = relache ? 2u : 0u;
            if ((fl & 2) != 0) f |= 1; // touche « étendue » (préfixe E0)
            tamponClavier[0].type = 1;
            if (vk == 0x13 || make == 0) { tamponClavier[0].ki.wVk = (ushort)vk; tamponClavier[0].ki.wScan = 0; }
            else { tamponClavier[0].ki.wVk = 0; tamponClavier[0].ki.wScan = (ushort)make; f |= 8; } // KEYEVENTF_SCANCODE : comme une vraie touche
            tamponClavier[0].ki.dwFlags = f;
            tamponClavier[0].ki.time = 0;
            tamponClavier[0].ki.dwExtraInfo = new IntPtr(MARQUE);
            Natif.SendInputClavier(1, tamponClavier, tailleInput);
            Interlocked.Increment(ref injEnvoyes);
        }

        // ---------------- appareils ----------------

        void SurDebranche(IntPtr dev)
        {
            lock (verrou)
            {
                string cle;
                if (!cles.TryGetValue(dev, out cle)) return;
                cles.Remove(dev);
                if (cles.ContainsValue(cle)) return; // une autre partie du même appareil est encore là
                Joueur j;
                if (parCle.TryGetValue(cle, out j)) { Journal.Ecrire("Souris débranchée : joueur " + j.Num); Retirer(j); }
                Clavier c;
                if (claviers.TryGetValue(cle, out c))
                {
                    Journal.Ecrire("Clavier n°" + c.Num + " débranché.");
                    if (c.Joueur != null) c.Joueur.Clavier = null;
                    claviers.Remove(cle);
                    MettreAJourMode();
                }
            }
        }

        /// Un appareil = une clé. Les souris « gamer » se présentent en plusieurs morceaux
        /// (MI_00, MI_01...) : on remonte jusqu'à l'appareil USB / Bluetooth commun.
        string CleDe(IntPtr dev)
        {
            string cle;
            if (cles.TryGetValue(dev, out cle)) return cle;
            uint lg = 1024;
            StringBuilder sb = new StringBuilder(1024);
            string nom = Natif.GetRawInputDeviceInfo(dev, 0x20000007, sb, ref lg) < 0x7FFFFFFF ? sb.ToString() : ""; // RIDI_DEVICENAME
            cle = "H" + dev.ToInt64().ToString("X");
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

        // =====================================================================
        // Joueurs et modes (sous verrou)
        // =====================================================================

        Joueur Premier()
        {
            for (int i = 1; i <= 4; i++) if (places[i] != null) return places[i];
            return null;
        }

        int Nombre()
        {
            int n = 0;
            for (int i = 1; i <= 4; i++) if (places[i] != null) n++;
            return n;
        }

        Joueur NouveauJoueur(string cle)
        {
            int num = 0;
            for (int i = 1; i <= 4; i++) if (places[i] == null) { num = i; break; }
            if (num == 0) return null;
            Joueur j = new Joueur();
            j.Num = num; j.Cle = cle;
            j.DerniereActivite = Environment.TickCount;
            j.DernierEvt = j.DernierMouvement = chrono.Elapsed.TotalMilliseconds;
            POINT p;
            Natif.GetCursorPos(out p);
            j.X = p.X; j.Y = p.Y;
            if (Nombre() > 0) { j.X += 70; j.Y += 50; Borner(j); }
            else porteur = j;
            places[num] = j;
            parCle[cle] = j;
            Journal.Ecrire("Nouvelle souris : joueur " + num + " = " + cle);
            MettreAJourMode();
            if (actif && j != porteur) Dire("Joueur " + num + " (" + Dessin.NomCouleur(reglages.CouleurDe(num)) + ") : à toi de jouer !");
            Signaler();
            return j;
        }

        void Retirer(Joueur j)
        {
            if (j == porteur) { RelacherTout(j); porteur = null; }
            if (j == focus) focus = null;
            if (j == associerPour) associerPour = null;
            if (j.Clavier != null) { j.Clavier.Joueur = null; j.Clavier = null; }
            places[j.Num] = null;
            parCle.Remove(j.Cle);
            retires.Add(j);
            fileClics.RemoveAll(delegate(Clic c) { return c.J == j; });
            if (fileClics.Count > 0 && actif && (porteur == null || porteur.Boutons == 0)) TraiterFile();
            MettreAJourMode();
            Signaler();
        }

        void MettreAJourMode()
        {
            bool nActif = !pause && secours == null && Nombre() >= 2;
            if (nActif != actif)
            {
                for (int i = 1; i <= 4; i++) if (places[i] != null) places[i].Boutons = 0;
                actif = nActif;
                if (actif)
                {
                    if (porteur == null || places[porteur.Num] != porteur) porteur = Premier();
                    if (porteur != null)
                    {
                        POINT p;
                        Natif.GetCursorPos(out p);
                        porteur.X = p.X; porteur.Y = p.Y;
                    }
                }
                Journal.Ecrire(actif ? "Mode plusieurs souris." : "Mode normal" + (pause ? " (pause)." : secours != null ? " (secours : " + secours + ")." : "."));
            }
            clavierActif = actif && claviers.Count >= 2;
            bloquerSouris = actif;
            bloquerClavier = clavierActif;
            if (!clavierActif) clavAttente = 0;
            Signaler();
        }

        public void BasculerPause()
        {
            lock (verrou)
            {
                pause = !pause;
                secours = null; secoursDefinitif = false;
                MettreAJourMode();
            }
            Dire(pause ? "En pause : toutes les souris et tous les claviers redeviennent normaux. Ctrl+Alt+F12 pour reprendre."
                       : "C'est reparti : un curseur par souris, un clavier par joueur.");
        }

        void Secours(string raison, bool definitif)
        {
            secours = raison;
            secoursDefinitif = definitif;
            secoursFenetre = Natif.GetForegroundWindow();
            secoursDebut = Environment.TickCount;
            MettreAJourMode();
        }

        // =====================================================================
        // Fil de l'interface : dessins et surveillance
        // =====================================================================

        Apparence ApparenceDe(int num)
        {
            if (apparences[num] == null)
                apparences[num] = Dessin.Fabriquer(fleche, reglages.CouleurDe(num), reglages.TailleDe(num), num);
            return apparences[num];
        }

        /// À appeler quand une couleur ou une taille change (le fil du dessin refait les images).
        public void RefaireDessins() { Interlocked.Increment(ref version); }

        /// Le fil du dessin : il attend chaque image affichée par l'écran (DwmFlush) puis déplace les curseurs.
        /// Les curseurs dessinés bougent ainsi aussi souvent que l'écran se rafraîchit, sans à-coups.
        void BoucleDessin()
        {
            double verif = 0;
            while (!finDessin)
            {
                Application.DoEvents();
                if (Natif.DwmFlush() != 0) Thread.Sleep(7);
                try { Dessiner(); }
                catch (Exception e) { Journal.Ecrire("Dessin : " + e.Message); Thread.Sleep(100); }
                double t = chrono.Elapsed.TotalMilliseconds;
                if (t - verif > 1000)
                {
                    verif = t;
                    for (int i = 1; i <= 4; i++)
                    {
                        Joueur j = places[i];
                        if (j != null) { if (j.Calque != null) j.Calque.Verifier(); if (j.Pastille != null) j.Pastille.Verifier(); }
                    }
                }
            }
            lock (verrou)
            {
                for (int i = 1; i <= 4; i++) if (places[i] != null) DetruireDessins(places[i]);
                foreach (Joueur j in retires) DetruireDessins(j);
            }
        }

        void DetruireDessins(Joueur j)
        {
            if (j.Calque != null) { j.Calque.Detruire(); j.Calque = null; }
            if (j.Pastille != null) { j.Pastille.Detruire(); j.Pastille = null; }
        }

        void Dessiner()
        {
            Joueur[] js = new Joueur[5];
            int[] xs = new int[5], ys = new int[5];
            bool act;
            Joueur port;
            List<Joueur> morts;
            lock (verrou)
            {
                act = actif;
                port = porteur;
                for (int i = 1; i <= 4; i++)
                    if ((js[i] = places[i]) != null) { xs[i] = (int)Math.Round(js[i].X); ys[i] = (int)Math.Round(js[i].Y); }
                morts = retires;
                retires = new List<Joueur>();
            }
            foreach (Joueur m in morts) DetruireDessins(m);
            if (versionVue != version)
            {
                versionVue = version;
                for (int i = 1; i <= 4; i++) { if (apparences[i] != null) { apparences[i].Image.Dispose(); apparences[i].Pastille.Dispose(); } apparences[i] = null; }
            }
            POINT cur;
            Natif.GetCursorPos(out cur);
            for (int i = 1; i <= 4; i++)
            {
                Joueur j = js[i];
                if (j == null) continue;
                if (!act)
                {
                    if (j.Calque != null) j.Calque.Cacher();
                    if (j.Pastille != null) j.Pastille.Cacher();
                    continue;
                }
                Apparence a = ApparenceDe(i);
                if (j.VersionDessin != versionVue)
                {
                    if (j.Calque == null) j.Calque = new Calque("joueur " + i);
                    if (j.Pastille == null) j.Pastille = new Calque("pastille " + i);
                    j.Calque.Image(a.Image);
                    j.Pastille.Image(a.Pastille);
                    j.VersionDessin = versionVue;
                }
                if (j == port)
                {
                    j.Calque.Cacher();
                    j.Pastille.Montrer(cur.X + a.PastilleX, cur.Y + a.PastilleY);
                }
                else
                {
                    j.Pastille.Cacher();
                    j.Calque.Montrer(xs[i] - a.Hx, ys[i] - a.Hy);
                }
            }
        }

        void Garde()
        {
            tours++;
            int phys = Interlocked.Exchange(ref crochetPhys, 0), bloques = Interlocked.Exchange(ref crochetPhysBloques, 0);
            int recus = Interlocked.Exchange(ref brutPhys, 0), envoyes = Interlocked.Exchange(ref injEnvoyes, 0), vus = Interlocked.Exchange(ref injVus, 0);
            string avertir = null;
            lock (verrou)
            {
                // Fin du mode secours temporaire.
                if (secours != null && !secoursDefinitif &&
                    (Natif.GetForegroundWindow() != secoursFenetre || Environment.TickCount - secoursDebut > 15000))
                {
                    secours = null;
                    MettreAJourMode();
                }
                if (actif)
                {
                    if (recus >= 20 && phys == 0)
                    {
                        // Windows a débranché nos crochets (ou une fenêtre administrateur est devant) : on les remet.
                        Journal.Ecrire("Crochets silencieux (" + recus + " gestes reçus) : réinstallation.");
                        Natif.PostMessage(fenCrochets.Handle, WM_APP_REINSTALLER, IntPtr.Zero, IntPtr.Zero);
                        Secours("crochets à réinstaller", false);
                    }
                    else if (bloques >= 30 && recus == 0)
                    {
                        Secours("souris non reconnue", true);
                        avertir = "Une souris n'est pas reconnue : retour au mode normal. Ctrl+Alt+F12 deux fois pour réessayer.";
                    }
                    else if (envoyes >= 12 && vus * 4 < envoyes)
                    {
                        Secours("fenêtre administrateur", false); // Windows ne laisse pas passer nos gestes
                    }
                    else if (clavierActif && clavAttente != 0 && Environment.TickCount - clavAttente > 600)
                    {
                        Secours("clavier non reconnu", true);
                        avertir = "Un clavier n'est pas reconnu : retour au mode normal. Ctrl+Alt+F12 deux fois pour réessayer.";
                    }
                    // Sécurité : un bouton « tenu » sans aucune nouvelle de la souris depuis 4 s est relâché.
                    if (porteur != null && porteur.Boutons != 0 && chrono.Elapsed.TotalMilliseconds - porteur.DernierEvt > 4000)
                    {
                        Journal.Ecrire("Bouton du joueur " + porteur.Num + " relâché par sécurité.");
                        RelacherTout(porteur);
                    }
                    if (fileClics.Count > 0 && (porteur == null || porteur.Boutons == 0)) TraiterFile();
                }
                if (tours % 4 == 0)
                {
                    for (int i = 1; i <= 4; i++)
                    {
                        Joueur j = places[i];
                        if (j != null && j != porteur && Environment.TickCount - j.DerniereActivite > OUBLI_MS)
                        {
                            Journal.Ecrire("Joueur " + j.Num + " oublié (souris immobile).");
                            Retirer(j);
                        }
                    }
                }
                if (tours % 20 == 0 && actif && (evtsSouris > 0 || evtsClavier > 0))
                {
                    Journal.Ecrire("5 s : " + evtsSouris + " gestes souris, " + evtsClavier + " touches, " + passages + " passages du curseur, porteur = joueur " +
                        (porteur != null ? porteur.Num.ToString() : "-") + ", clavier " + (clavierActif ? "par joueur" : "partagé") +
                        ", retard max " + maxRetard.ToString("0") + " ms, SendInput max " + maxEnvoi.ToString("0.0") + " ms.");
                }
                if (tours % 20 == 0) { evtsSouris = 0; evtsClavier = 0; passages = 0; maxRetard = 0; maxEnvoi = 0; }
            }
            if (avertir != null) Dire(avertir);
            if (tours % 8 == 0)
            {
                ImageCurseur f = Dessin.FlecheWindows();
                if (f != null && !f.MemesPixels(fleche)) { fleche = f; RefaireDessins(); Journal.Ecrire("La flèche de Windows a changé."); }
            }
            if (tours % 16 == 0) LireReglagesWindows();
        }

        void LireReglagesWindows()
        {
            int v = 10;
            if (Natif.SystemParametersInfo(0x0070, 0, ref v, 0) && v >= 1 && v <= 20) facteurWindows = VitessesWindows[v]; // SPI_GETMOUSESPEED
            int[] souris = new int[3];
            if (Natif.SystemParametersInfo(0x0003, 0, souris, 0)) precision = souris[2] != 0; // SPI_GETMOUSE
        }

        // =====================================================================
        // Pour le menu
        // =====================================================================

        public bool EnPause { get { return pause; } }

        public string Etat()
        {
            lock (verrou)
            {
                int n = Nombre();
                if (pause) return "En pause (Ctrl+Alt+F12 pour reprendre)";
                if (secours != null) return "Mode normal : " + secours;
                if (n < 2) return "Une seule souris : bouge une 2e souris !";
                return n + " souris, " + claviers.Count + " clavier" + (claviers.Count > 1 ? "s" : "") + (clavierActif ? " (un par joueur)" : " (partagé)");
            }
        }

        public sealed class Info { public int Num; public bool Porteur; public int Clavier; public bool AttendClavier; }

        public List<Info> Joueurs()
        {
            List<Info> l = new List<Info>();
            lock (verrou)
                for (int i = 1; i <= 4; i++)
                    if (places[i] != null)
                    {
                        Info f = new Info();
                        f.Num = i;
                        f.Porteur = places[i] == porteur && actif;
                        f.Clavier = places[i].Clavier != null ? places[i].Clavier.Num : 0;
                        f.AttendClavier = associerPour == places[i];
                        l.Add(f);
                    }
            return l;
        }

        public void ChoisirClavier(int num)
        {
            lock (verrou) associerPour = places[num];
            Dire("Joueur " + num + " : appuie sur une touche de ton clavier.");
        }

        public void EnleverClavier(int num)
        {
            lock (verrou)
            {
                Joueur j = places[num];
                if (j != null && j.Clavier != null) { j.Clavier.Joueur = null; j.Clavier = null; }
            }
            Signaler();
        }

        /// Oublie toutes les souris sauf celle qui a le vrai curseur.
        public void Reinitialiser()
        {
            lock (verrou)
            {
                Joueur garde = porteur != null ? porteur : Premier();
                for (int i = 1; i <= 4; i++) if (places[i] != null && places[i] != garde) Retirer(places[i]);
            }
        }

        void Signaler()
        {
            if (Change != null && ui != null) ui.Post(delegate { if (Change != null) Change(); }, null);
        }

        void Dire(string s)
        {
            if (Message != null && ui != null) ui.Post(delegate { if (Message != null) Message(s); }, null);
        }
    }
}
