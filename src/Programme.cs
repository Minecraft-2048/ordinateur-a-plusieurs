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
        public const string Version = "1.0.0";

        [STAThread]
        static void Main(string[] args)
        {
            List<string> a = new List<string>(args);
            int i = a.IndexOf("--icone");
            if (i >= 0 && i + 1 < a.Count) { Dessin.EcrireIco(a[i + 1]); return; }

            bool premier;
            using (Mutex unique = new Mutex(true, "OrdinateurAPlusieurs-unique", out premier))
            {
                if (a.Contains("--quitter"))
                {
                    if (!premier)
                    {
                        EventWaitHandle ev;
                        if (EventWaitHandle.TryOpenExisting("OrdinateurAPlusieurs-quitter", out ev)) { ev.Set(); ev.Dispose(); }
                    }
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

        readonly Reglages reglages = Reglages.Charger();
        readonly Moteur moteur;
        readonly NotifyIcon icone = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem etat, pause, demarrage;
        readonly ToolStripMenuItem[] details = new ToolStripMenuItem[4];
        readonly EventWaitHandle quitter = new EventWaitHandle(false, EventResetMode.AutoReset, "OrdinateurAPlusieurs-quitter");
        readonly System.Windows.Forms.Timer minuteQuitter = new System.Windows.Forms.Timer();

        public Appli()
        {
            Journal.Actif = reglages.Journal;
            moteur = new Moteur(reglages);

            etat = new ToolStripMenuItem(Programme.Nom);
            etat.Enabled = false;
            menu.Items.Add(etat);
            for (int i = 0; i < 4; i++)
            {
                details[i] = new ToolStripMenuItem("");
                details[i].Enabled = false;
                menu.Items.Add(details[i]);
            }
            menu.Items.Add(new ToolStripSeparator());
            pause = new ToolStripMenuItem("Pause   (Ctrl+Alt+F12)", null, delegate { moteur.BasculerPause(); });
            menu.Items.Add(pause);

            ToolStripMenuItem vitesse = new ToolStripMenuItem("Vitesse des curseurs dessinés");
            AjouterChoix(vitesse, new string[] { "Lente", "Normale", "Rapide", "Très rapide" }, new double[] { 0.6, 1.0, 1.6, 2.3 },
                delegate { return reglages.Vitesse; }, delegate(double v) { reglages.Vitesse = v; reglages.Enregistrer(); });
            menu.Items.Add(vitesse);
            ToolStripMenuItem taille = new ToolStripMenuItem("Taille des curseurs dessinés");
            AjouterChoix(taille, new string[] { "Normale", "Grande", "Très grande" }, new double[] { 1.0, 1.4, 1.9 },
                delegate { return reglages.Taille; }, delegate(double v) { reglages.Taille = (float)v; reglages.Enregistrer(); moteur.ChangerTaille(); });
            menu.Items.Add(taille);
            menu.Items.Add(new ToolStripMenuItem("Oublier les autres souris", null, delegate { moteur.Reinitialiser(); }));
            demarrage = new ToolStripMenuItem("Lancer avec Windows", null, delegate { BasculerDemarrage(); });
            menu.Items.Add(demarrage);
            menu.Items.Add(new ToolStripMenuItem("Comment ça marche ?", null, delegate { Aide(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quitter", null, delegate { ExitThread(); }));
            menu.Opening += delegate { MettreAJourMenu(); };

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
                "Branche une 2e souris et bouge-la : un curseur de couleur apparaît pour elle. Ctrl+Alt+F12 = pause.", ToolTipIcon.None);
        }

        delegate double Lire();
        delegate void Ecrire(double v);

        void AjouterChoix(ToolStripMenuItem parent, string[] noms, double[] valeurs, Lire lire, Ecrire ecrire)
        {
            for (int i = 0; i < noms.Length; i++)
            {
                double v = valeurs[i];
                ToolStripMenuItem choix = new ToolStripMenuItem(noms[i]);
                choix.Click += delegate { ecrire(v); };
                parent.DropDownItems.Add(choix);
            }
            parent.DropDownOpening += delegate
            {
                for (int i = 0; i < valeurs.Length; i++)
                    ((ToolStripMenuItem)parent.DropDownItems[i]).Checked = Math.Abs(lire() - valeurs[i]) < 0.01;
            };
        }

        void MettreAJourMenu()
        {
            etat.Text = moteur.Etat();
            List<string> d = moteur.Details();
            for (int i = 0; i < 4; i++)
            {
                details[i].Visible = i < d.Count;
                if (i < d.Count) details[i].Text = d[i];
            }
            pause.Checked = moteur.EnPause;
            demarrage.Checked = LanceAvecWindows();
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
                "Branche jusqu'à 4 souris sur le PC. Dès qu'une souris bouge, elle reçoit son curseur :\n\n" +
                "   Joueur 1 : bleu (c'est le vrai curseur de Windows)\n" +
                "   Joueur 2 : orange\n   Joueur 3 : vert\n   Joueur 4 : violet\n\n" +
                "Chacun peut cliquer, glisser-déposer et tourner la molette à l'endroit de son curseur. " +
                "Pendant le clic d'un joueur, le vrai curseur de Windows saute chez lui puis revient tout seul.\n\n" +
                "Le clavier est partagé : il écrit dans la dernière fenêtre cliquée.\n\n" +
                "Ctrl+Alt+F12 : pause (toutes les souris bougent de nouveau le même curseur).\n" +
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
        public double Vitesse = 1.0;
        public float Taille = 1.0f;
        public bool Journal;

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
                    double d;
                    if (cle == "vitesse" && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.Vitesse = Math.Max(0.2, Math.Min(4, d));
                    else if (cle == "taille" && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.Taille = (float)Math.Max(0.6, Math.Min(3, d));
                    else if (cle == "journal") r.Journal = val == "1";
                }
            }
            catch { }
            return r;
        }

        public void Enregistrer()
        {
            try
            {
                Directory.CreateDirectory(Dossier);
                File.WriteAllLines(Fichier, new string[] {
                    "vitesse=" + Vitesse.ToString(CultureInfo.InvariantCulture),
                    "taille=" + Taille.ToString(CultureInfo.InvariantCulture),
                    "journal=" + (Journal ? "1" : "0") });
            }
            catch { }
        }
    }

    static class Journal
    {
        public static bool Actif;

        public static void Ecrire(string s)
        {
            if (!Actif) return;
            try
            {
                Directory.CreateDirectory(Reglages.Dossier);
                File.AppendAllText(Path.Combine(Reglages.Dossier, "journal.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
            }
            catch { }
        }
    }
}
