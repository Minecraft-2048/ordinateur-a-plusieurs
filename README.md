# Ordinateur à plusieurs

**Plusieurs souris, plusieurs curseurs, un seul PC.** Branche 2 à 4 souris sur un ordinateur Windows :
chaque souris reçoit son propre curseur de couleur, et plusieurs personnes peuvent se servir du même PC en même temps.

![Les curseurs des joueurs](assets/curseurs.png)

| Joueur | Curseur |
|---|---|
| 1 | bleu : c'est le vrai curseur de Windows |
| 2 | orange |
| 3 | vert |
| 4 | violet |

## Installation

1. Télécharge `MultiSouris.exe` dans les [Releases](../../releases/latest).
2. Lance-le : une icône avec deux curseurs apparaît près de l'horloge.
3. Branche une 2e souris et bouge-la : son curseur orange apparaît.

Il n'y a rien à installer (Windows 10 ou 11, .NET Framework 4 déjà présent dans Windows).
L'exe n'est pas signé : Windows SmartScreen peut afficher « Windows a protégé votre ordinateur » → *Informations complémentaires* → *Exécuter quand même*.

## Ce que chaque joueur peut faire

- bouger son curseur, **cliquer** (gauche, droit, milieu, boutons de côté) ;
- **glisser-déposer** (déplacer une fenêtre, sélectionner du texte...) ;
- tourner la **molette**.

Windows n'a qu'un seul vrai curseur. Quand le joueur 2, 3 ou 4 clique, le vrai curseur saute chez lui le temps
du clic, puis revient tout seul chez le joueur 1. Pendant ce temps, le joueur 1 garde un curseur bleu dessiné.

Le **clavier est partagé** : il écrit dans la dernière fenêtre cliquée, quel que soit le joueur.

## Raccourcis et menu

- **Ctrl+Alt+F12** : pause (toutes les souris bougent de nouveau le même curseur) / reprise.
- Clic droit sur l'icône : vitesse et taille des curseurs dessinés, oublier les autres souris, lancer avec Windows, quitter.
- Une souris qui ne bouge pas pendant 10 minutes perd son curseur ; il revient dès qu'elle bouge.
- Un ordinateur portable compte son pavé tactile comme une souris : pavé tactile + une souris = 2 joueurs.

## Sécurité

L'appli ne laisse jamais une souris bloquée :

- avec une seule souris, elle ne touche à rien ;
- si une souris n'est pas reconnue, elle repasse en mode normal ;
- devant une fenêtre « administrateur » (Gestionnaire des tâches...), elle repasse en mode normal le temps
  que cette fenêtre reste au premier plan. Pour que tout marche aussi dans ces fenêtres, lance `MultiSouris.exe` en administrateur.

## Comment ça marche

- **Raw Input** (`RegisterRawInputDevices`) indique quelle souris a bougé. Les souris « gamer » qui se présentent
  en plusieurs morceaux (`MI_00`, `MI_01`...) sont regroupées en remontant jusqu'à l'appareil USB ou Bluetooth commun.
- Dès qu'il y a 2 joueurs, un crochet bas niveau (`WH_MOUSE_LL`) bloque les gestes des vraies souris.
  L'appli renvoie elle-même ceux du joueur 1 (`SendInput`), avec la vitesse et l'accélération de Windows.
- Les autres joueurs ont une fenêtre transparente, toujours au premier plan, qui porte leur curseur.
  Leurs clics passent par le vrai curseur, posé un instant à leur place.

## Compiler

```powershell
.\construire.ps1
```

Il utilise le compilateur C# livré avec Windows (`csc.exe` du .NET Framework 4), sans rien d'autre à installer.
`.\construire.ps1 -Relancer` arrête l'appli, recompile et la relance.

Journal pour le dépannage : mettre `journal=1` dans `%LOCALAPPDATA%\OrdinateurAPlusieurs\reglages.txt`,
le journal s'écrit dans `journal.txt` à côté.

## Licence

MIT
