using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OrdinateurAPlusieurs
{
    static class Programme
    {
        public const string Nom = "Ordinateur à plusieurs";
        public const string Version = "1.1.0";

        [STAThread]
        static void Main(string[] args)
        {
            List<string> a = new List<string>(args);
            Moteur.Essai = a.Contains("--essai");
            int i = a.IndexOf("--icone");
            if (i >= 0 && i + 1 < a.Count) { Dessin.EcrireIco(a[i + 1]); return; }

            bool premier;
            using (Mutex unique = new Mutex(true, "OrdinateurAPlusieurs-unique", out premier))
            {
                if (a.Contains("--quitter"))
                {
                    EventWaitHandle ev;
                    if (!premier && EventWaitHandle.TryOpenExisting("OrdinateurAPlusieurs-quitter", out ev)) { ev.Set(); ev.Dispose(); }
                    return;
                }
                if (!premier)
                {
                    MessageBox.Show(Nom + " est déjà lancé : son icône (deux curseurs) est près de l'horloge.", Nom,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new Appli()); }
                catch (Exception e)
                {
                    Journal.Ecrire("Erreur : " + e);
                    MessageBox.Show(e.Message, Nom, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    sealed class Appli : ApplicationContext
    {
        const string CleDemarrage = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string NomDemarrage = "OrdinateurAPlusieurs";

        static readonly string[] NomsVitesses = { "Comme le curseur principal", "Lente", "Un peu lente", "Un peu rapide", "Rapide", "Très rapide" };
        static readonly double[] Vitesses = { 1.0, 0.6, 0.8, 1.25, 1.6, 2.2 };
        static readonly string[] NomsTailles = { "Comme le curseur principal", "Petite", "Grande", "Très grande" };
        static readonly double[] Tailles = { 1.0, 0.75, 1.5, 2.0 };

        readonly Reglages reglages;
        readonly Moteur moteur;
        readonly NotifyIcon icone = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly EventWaitHandle quitter = new EventWaitHandle(false, EventResetMode.AutoReset, "OrdinateurAPlusieurs-quitter");
        readonly System.Windows.Forms.Timer minuteQuitter = new System.Windows.Forms.Timer();

        public Appli()
        {
            reglages = Reglages.Charger();
            Journal.Actif = reglages.Journal;
            moteur = new Moteur(reglages);

            menu.Items.Add("...");
            menu.Opening += delegate { RemplirMenu(); };

            int cote = Math.Max(16, (int)(16 * Natif.Echelle()));
            using (Bitmap b = Dessin.Icone(cote)) icone.Icon = Icon.FromHandle(b.GetHicon());
            icone.Text = Programme.Nom;
            icone.ContextMenuStrip = menu;
            icone.DoubleClick += delegate { Aide(); };
            icone.Visible = true;

            moteur.Change += delegate { MettreAJourInfobulle(); };
            moteur.Message += delegate(string s) { icone.ShowBalloonTip(4000, Programme.Nom, s, ToolTipIcon.None); };
            moteur.Demarrer();
            MettreAJourInfobulle();

            minuteQuitter.Interval = 500;
            minuteQuitter.Tick += delegate { if (quitter.WaitOne(0)) ExitThread(); };
            minuteQuitter.Start();

            icone.ShowBalloonTip(5000, Programme.Nom,
                "Branche une 2e souris et bouge-la : un curseur de couleur apparaît pour elle. Clic droit sur cette icône pour les réglages de chaque joueur.", ToolTipIcon.None);
        }

        // ---------- menu (refait à chaque ouverture) ----------

        void RemplirMenu()
        {
            menu.Items.Clear();
            ToolStripMenuItem etat = new ToolStripMenuItem(moteur.Etat());
            etat.Enabled = false;
            menu.Items.Add(etat);
            foreach (Moteur.Info j in moteur.Joueurs()) menu.Items.Add(MenuJoueur(j));
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem pause = new ToolStripMenuItem("Pause   (Ctrl+Alt+F12)", null, delegate { moteur.BasculerPause(); });
            pause.Checked = moteur.EnPause;
            menu.Items.Add(pause);
            menu.Items.Add(new ToolStripMenuItem("Oublier les autres souris", null, delegate { moteur.Reinitialiser(); }));
            ToolStripMenuItem demarrage = new ToolStripMenuItem("Lancer avec Windows", null, delegate { BasculerDemarrage(); });
            demarrage.Checked = LanceAvecWindows();
            menu.Items.Add(demarrage);
            menu.Items.Add(new ToolStripMenuItem("Comment ça marche ?", null, delegate { Aide(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quitter", null, delegate { ExitThread(); }));
        }

        ToolStripMenuItem MenuJoueur(Moteur.Info j)
        {
            int n = j.Num;
            Color c = reglages.CouleurDe(n);
            string titre = "Joueur " + n + " : curseur " + Dessin.NomCouleur(c);
            if (j.Clavier > 0) titre += ", clavier n°" + j.Clavier;
            if (j.Porteur) titre += "  (a le vrai curseur)";
            ToolStripMenuItem m = new ToolStripMenuItem(titre, Dessin.Echantillon(c));

            ToolStripMenuItem couleur = new ToolStripMenuItem("Couleur");
            for (int i = 0; i < Dessin.Palette.Length; i++)
            {
                Color p = Dessin.Palette[i];
                ToolStripMenuItem choix = new ToolStripMenuItem(Majuscule(Dessin.NomsCouleurs[i]), Dessin.Echantillon(p), delegate { reglages.ChangerCouleur(n, p); Applique(); });
                choix.Checked = p.ToArgb() == c.ToArgb();
                couleur.DropDownItems.Add(choix);
            }
            m.DropDownItems.Add(couleur);

            ToolStripMenuItem vitesse = new ToolStripMenuItem("Vitesse");
            for (int i = 0; i < Vitesses.Length; i++)
            {
                double v = Vitesses[i];
                ToolStripMenuItem choix = new ToolStripMenuItem(NomsVitesses[i], null, delegate { reglages.ChangerVitesse(n, v); Applique(); });
                choix.Checked = Math.Abs(reglages.VitesseDe(n) - v) < 0.01;
                vitesse.DropDownItems.Add(choix);
            }
            m.DropDownItems.Add(vitesse);

            ToolStripMenuItem taille = new ToolStripMenuItem("Taille du curseur dessiné");
            for (int i = 0; i < Tailles.Length; i++)
            {
                double t = Tailles[i];
                ToolStripMenuItem choix = new ToolStripMenuItem(NomsTailles[i], null, delegate { reglages.ChangerTaille(n, (float)t); Applique(); });
                choix.Checked = Math.Abs(reglages.TailleDe(n) - t) < 0.01;
                taille.DropDownItems.Add(choix);
            }
            m.DropDownItems.Add(taille);

            ToolStripMenuItem clavier = new ToolStripMenuItem("Clavier");
            ToolStripMenuItem info = new ToolStripMenuItem(j.AttendClavier ? "En attente : appuie sur une touche du clavier de ce joueur"
                : j.Clavier > 0 ? "Clavier n°" + j.Clavier : "Pas de clavier (le premier clavier utilisé après un clic lui revient)");
            info.Enabled = false;
            clavier.DropDownItems.Add(info);
            clavier.DropDownItems.Add(new ToolStripMenuItem("Choisir son clavier : appuyer sur une de ses touches", null, delegate { moteur.ChoisirClavier(n); }));
            if (j.Clavier > 0) clavier.DropDownItems.Add(new ToolStripMenuItem("Enlever le clavier", null, delegate { moteur.EnleverClavier(n); }));
            m.DropDownItems.Add(clavier);

            m.DropDownItems.Add(new ToolStripSeparator());
            m.DropDownItems.Add(new ToolStripMenuItem("Tout remettre comme le curseur principal", null, delegate { reglages.Remettre(n); Applique(); }));
            return m;
        }

        static string Majuscule(string s) { return s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1); }

        void Applique()
        {
            reglages.Enregistrer();
            moteur.RefaireDessins();
        }

        void MettreAJourInfobulle()
        {
            string t = Programme.Nom + " - " + moteur.Etat();
            icone.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        bool LanceAvecWindows()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CleDemarrage))
                return k != null && k.GetValue(NomDemarrage) != null;
        }

        void BasculerDemarrage()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CleDemarrage))
            {
                if (k.GetValue(NomDemarrage) != null) k.DeleteValue(NomDemarrage, false);
                else k.SetValue(NomDemarrage, "\"" + Application.ExecutablePath + "\"");
            }
        }

        void Aide()
        {
            MessageBox.Show(
                "Branche jusqu'à 4 souris et 4 claviers. Dès qu'une souris bouge, elle reçoit son curseur de couleur, " +
                "avec son numéro de joueur.\n\n" +
                "Le vrai curseur de Windows va au joueur qui bouge : c'est lui qui voit les boutons s'allumer au survol " +
                "(la croix rouge des fenêtres...). Dès qu'il s'arrête un instant, ou qu'un autre joueur clique, le vrai curseur passe chez l'autre.\n\n" +
                "Claviers : chaque clavier va avec une souris. Un clavier n'écrit qu'à l'endroit choisi par la souris de son joueur : " +
                "si un autre joueur a cliqué dans une zone de texte, ton clavier n'y écrit pas. " +
                "Le premier clavier utilisé après le clic d'un joueur lui revient (on peut changer dans le menu).\n\n" +
                "Clic droit sur l'icône : couleur, vitesse, taille et clavier de chaque joueur. " +
                "Au départ, chaque curseur a la vitesse et la taille du curseur de Windows.\n\n" +
                "Ctrl+Alt+F12 : pause (souris et claviers redeviennent normaux).\n" +
                "Une souris qui ne bouge pas pendant 10 minutes perd son curseur ; il revient dès qu'elle bouge.\n\n" +
                "Version " + Programme.Version,
                Programme.Nom, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void ExitThreadCore()
        {
            minuteQuitter.Stop();
            moteur.Arreter();
            icone.Visible = false;
            icone.Dispose();
            base.ExitThreadCore();
        }
    }

    sealed class Reglages
    {
        static readonly Color[] CouleursDepart = { Color.Empty, Dessin.Palette[0], Dessin.Palette[1], Dessin.Palette[2], Dessin.Palette[3] };
        readonly int[] couleurs = new int[5];
        readonly double[] vitesses = { 1, 1, 1, 1, 1 };
        readonly float[] tailles = { 1, 1, 1, 1, 1 };
        public bool Journal;

        public Reglages() { for (int i = 1; i <= 4; i++) couleurs[i] = CouleursDepart[i].ToArgb(); }

        public Color CouleurDe(int n) { lock (this) return Color.FromArgb(couleurs[n]); }
        public double VitesseDe(int n) { lock (this) return vitesses[n]; }
        public float TailleDe(int n) { lock (this) return tailles[n]; }
        public void ChangerCouleur(int n, Color c) { lock (this) couleurs[n] = c.ToArgb(); }
        public void ChangerVitesse(int n, double v) { lock (this) vitesses[n] = Math.Max(0.2, Math.Min(4, v)); }
        public void ChangerTaille(int n, float t) { lock (this) tailles[n] = Math.Max(0.5f, Math.Min(3, t)); }
        public void Remettre(int n) { lock (this) { couleurs[n] = CouleursDepart[n].ToArgb(); vitesses[n] = 1; tailles[n] = 1; } }

        public static string Dossier
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrdinateurAPlusieurs"); }
        }

        static string Fichier { get { return Path.Combine(Dossier, "reglages.txt"); } }

        public static Reglages Charger()
        {
            Reglages r = new Reglages();
            try
            {
                if (!File.Exists(Fichier)) return r;
                foreach (string ligne in File.ReadAllLines(Fichier))
                {
                    int eg = ligne.IndexOf('=');
                    if (eg < 0) continue;
                    string cle = ligne.Substring(0, eg).Trim().ToLowerInvariant(), val = ligne.Substring(eg + 1).Trim();
                    if (cle == "journal") { r.Journal = val == "1"; continue; }
                    if (cle.Length < 2) continue;
                    int n = cle[cle.Length - 1] - '0';
                    if (n < 1 || n > 4) continue;
                    string nom = cle.Substring(0, cle.Length - 1);
                    double d; int c;
                    if (nom == "couleur" && int.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out c)) r.couleurs[n] = c | unchecked((int)0xFF000000);
                    else if (nom == "vitesse" && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.ChangerVitesse(n, d);
                    else if (nom == "taille" && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.ChangerTaille(n, (float)d);
                }
            }
            catch { }
            return r;
        }

        public void Enregistrer()
        {
            try
            {
                List<string> l = new List<string>();
                lock (this)
                    for (int n = 1; n <= 4; n++)
                    {
                        l.Add("couleur" + n + "=" + (couleurs[n] & 0xFFFFFF).ToString("X6"));
                        l.Add("vitesse" + n + "=" + vitesses[n].ToString(CultureInfo.InvariantCulture));
                        l.Add("taille" + n + "=" + tailles[n].ToString(CultureInfo.InvariantCulture));
                    }
                l.Add("journal=" + (Journal ? "1" : "0"));
                Directory.CreateDirectory(Dossier);
                File.WriteAllLines(Fichier, l.ToArray());
            }
            catch { }
        }
    }

    /// Journal de dépannage : dans le dossier des réglages si journal=1,
    /// ou à côté de l'exe si un fichier « journal.on » s'y trouve.
    static class Journal
    {
        public static bool Actif;
        static readonly object verrou = new object();
        static readonly string aCote = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "journal.txt");
        static readonly bool aCoteActif = File.Exists(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "journal.on"));

        public static void Ecrire(string s)
        {
            if (!Actif && !aCoteActif) return;
            try
            {
                string chemin = aCoteActif ? aCote : Path.Combine(Reglages.Dossier, "journal.txt");
                lock (verrou)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(chemin));
                    File.AppendAllText(chemin, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
