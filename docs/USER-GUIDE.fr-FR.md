# Portal AI Host : guide de l'utilisateur

Portal AI Host fait tourner un modèle d'IA sur votre PC et le partage avec vos applications Portal. Vos questions et vos fichiers restent sur vos propres appareils. Ce guide explique chaque partie de l'application, à quoi servent ses réglages et que faire lorsque quelque chose ne va pas.

Les emplacements où une image doit figurer sont marqués `[screenshot: ...]`.

## Sommaire

1. Démarrage rapide
2. Configuration requise, installation, mise à jour, désinstallation
3. Se repérer dans l'application
4. Modèles
5. Applications et autorisations
6. Essayer
7. Réglages de performance
8. Notifications
9. Permettre à d'autres appareils d'utiliser ce PC
10. Appareils sur ce PC
11. Contrôle des modèles et deux modèles à la fois
12. Utiliser votre PC domestique depuis un ordinateur portable (mode client)
13. Hors de chez vous : le relais et la clé d'accès au relais
14. Confidentialité et rapports d'erreur
15. Où sont stockées vos données, sauvegarde et suppression
16. Raccourcis clavier
17. Dépannage et FAQ
18. À venir
19. Assistance

---

## 1. Démarrage rapide

1. Installez Portal AI Host depuis le Microsoft Store, puis ouvrez-le.
2. La fenêtre d'accueil vérifie votre PC (mémoire, carte graphique, espace disque libre) et recommande un modèle. Choisissez **Démarrer avec les réglages recommandés**, ou **Télécharger** pour choisir le modèle vous-même. **Choisir plus tard** passe cette étape.
3. Repérez l'icône près de l'horloge. Portal AI Host attend discrètement à cet endroit. Cliquez dessus pour un aperçu rapide de l'état.
4. Ouvrez **Essayer** et posez une courte question pour vérifier que le modèle fonctionne.
5. La première fois qu'une application Portal souhaite utiliser l'IA, une invite vous demande de l'autoriser ou de la refuser.

[screenshot: la fenêtre d'accueil avec le modèle recommandé]

Si c'est plutôt l'IA de votre PC domestique que doit utiliser un ordinateur portable, choisissez **Utiliser mon PC domestique** dans la fenêtre d'accueil. Voir la section 12.

**Gratuit ou payant :** Portal AI Host est gratuit. Aucun compte, aucune connexion et aucun achat ne sont nécessaires pour les fonctions décrites dans ce guide.

---

## 2. Configuration requise, installation, mise à jour, désinstallation

### Configuration requise

| | Minimum | Recommandé |
|---|---|---|
| Windows | Windows 11 (21H2, build 22000) ou ultérieur, x64 ou Arm64 | Identique |
| Mémoire | 8 Go | 16 Go ou plus (32 Go ou plus pour les grands modèles) |
| Graphismes | Aucune (le modèle tourne alors sur le processeur, plus lentement) | Une carte graphique compatible Vulkan avec 8 Go de mémoire vidéo ou plus (AMD, NVIDIA ou Intel) |
| Espace disque libre | 5 Go | 25 Go |

Les modèles pèsent de 2 à 20 Go chacun. Les PC Arm64 exécutent le modèle sur le processeur. Une connexion Internet n'est nécessaire que pour télécharger des modèles et pour le relais ; une fois un modèle sur votre PC, il fonctionne hors ligne.

### Installation

Installez l'application depuis le Microsoft Store. Rien d'autre n'est nécessaire : ni Python, ni ligne de commande, ni autre logiciel. Le moteur d'IA fait partie de l'application.

### Démarrage avec Windows

**Paramètres > Général > Démarrer avec Windows** (par défaut : désactivé, sauf si vous choisissez « Démarrer avec les réglages recommandés » dans la fenêtre d'accueil, qui l'active). Lorsqu'il est activé, Portal AI Host attend dans la zone de notification après votre connexion, pour que les applications puissent l'utiliser immédiatement.
- Si le bouton indique qu'il n'est disponible que dans l'application installée, vous utilisez une copie de développement.
- Si vous l'avez désactivé dans le Gestionnaire des tâches (applications de démarrage), réactivez-le à cet endroit.
- Si votre organisation le définit, le bouton est verrouillé.

### Mise à jour

Le Store met l'application à jour. Après une mise à jour, **Nouveautés** s'ouvre une fois. Vous pouvez le rouvrir depuis le menu de l'application. Les modèles restent à leur emplacement d'une mise à jour à l'autre.

### Désinstallation

Supprimez l'application dans **Paramètres Windows > Applications**. Les modèles du dossier par défaut sont stockés dans le dossier de données de l'application et sont supprimés avec elle. Un dossier de modèles personnalisé que vous avez choisi n'est pas touché. Consultez la section 15 avant de désinstaller si vous voulez conserver quelque chose.

---

## 3. Se repérer dans l'application

Le côté gauche comporte cinq pages.

| Page | Rôle | Raccourci |
|---|---|---|
| Accueil | État, modèle actif, utilisation de la mémoire, application qui utilise l'IA en ce moment | Ctrl+1 |
| Modèles | Télécharger, utiliser et supprimer des modèles | Ctrl+2 |
| Appareils et applications | Autoriser ou retirer des applications Portal, les suspendre, définir la priorité | Ctrl+3 |
| Essayer | Poser une courte question au modèle | Ctrl+4 |
| Paramètres | Tout ce qui est configurable | Ctrl+, |

Une sixième page, **Appareils de ce PC**, liste les ordinateurs portables et téléphones associés à ce PC (voir section 10). Le menu de l'application contient Paramètres, Raccourcis clavier, Nouveautés et À propos.

**Accueil** indique si l'IA est **Prête**, **En train de répondre**, **Préparation du modèle**, **En veille** (le modèle se réveille lorsqu'une application le demande) ou n'a **pas encore de modèle**. **EN COURS** indique quelle application reçoit une réponse et environ combien de mots par seconde sont produits. Il affiche aussi la mémoire et la mémoire graphique utilisées par le modèle, ainsi qu'un bouton **Charger maintenant** / **Décharger maintenant**. **AUJOURD'HUI** compte les requêtes de chaque application. Une carte d'état affiche « Tout va bien » ou liste ce qui demande votre attention, avec une correction en un clic comme Charger le modèle, Redémarrer le moteur, Vérifier les fichiers ou Ouvrir les réglages de performance.

[screenshot: la page Accueil pendant qu'une application utilise le modèle]

L'icône de la zone de notification ouvre une petite fenêtre : modèle, mémoire, dernière requête et file d'attente, avec les boutons **Ouvrir l'application** et **Décharger le modèle**.

[screenshot: la fenêtre contextuelle de la zone de notification]

Si le programme hôte n'est pas en cours d'exécution, l'accueil propose **Démarrer Portal AI Host**. S'il s'est fermé de façon inattendue plus d'une fois, une barre **Mode sans échec** indique qu'il a démarré avec des réglages prudents. Vos applications continuent de fonctionner ; redémarrez le PC si le problème se répète.

---

## 4. Modèles

Un modèle est l'IA qui fait le travail. **Modèles** (Ctrl+2) affiche ce que vous avez et ce que vous pouvez ajouter.

### Modèle recommandé

Le premier lancement et la page d'accueil recommandent « le meilleur modèle pour ce PC », avec une raison claire, en fonction de votre mémoire, de votre mémoire graphique et de votre espace disque libre. Lorsqu'un meilleur modèle devient disponible, une barre indique **Un meilleur modèle est disponible**. Votre modèle actuel reste en place jusqu'à ce que le nouveau soit prêt, et vous choisissez quand supprimer l'ancien.

### Télécharger un modèle

1. Ouvrez **Modèles**. Choisissez un modèle sous **Modèles que vous pouvez ajouter**.
2. Appuyez sur **Télécharger**. Une barre indique la progression et le temps restant.
3. **Pause** et **Reprendre** sont disponibles à tout moment. **Arrêter** abandonne la partie déjà téléchargée (après confirmation).
4. L'hôte vérifie les fichiers téléchargés avant de les utiliser.
5. Une fois le téléchargement terminé, appuyez sur **Utiliser ce modèle**.

Les téléchargements peuvent reprendre après un redémarrage et sont vérifiés fichier par fichier. Un téléchargement nécessite assez d'espace disque libre ; sinon, la page indique le manque et propose **Changer de dossier dans les Paramètres**. Le PC reste éveillé pendant un téléchargement (Paramètres > Performance, activé par défaut).

[screenshot: la page Modèles avec un téléchargement en cours]

Un modèle marqué « Ce modèle est peut-être trop volumineux pour ce PC » peut tout de même être téléchargé, mais il pourra fonctionner lentement ou ne pas tenir.

### Détails, vérification des fichiers, nouveau téléchargement, suppression

- **Détails** indique à quoi sert le modèle, ce qu'il faut savoir, la mémoire qu'il nécessite, sa licence, la date de sa dernière utilisation et l'espace qu'il occupe.
- **Vérifier les fichiers** contrôle à nouveau un modèle sur le disque. Cette action ne supprime jamais rien. Un modèle endommagé est marqué **Endommagé**.
- **Télécharger à nouveau** (pour un modèle endommagé) supprime la copie endommagée et la télécharge de nouveau. Rien d'autre n'est supprimé.
- **Supprimer** retire un modèle de ce PC après une confirmation indiquant l'espace libéré. Si le modèle est en cours d'utilisation, l'hôte le décharge d'abord et vous demande quoi utiliser ensuite. Seuls les fichiers téléchargés par l'hôte sont supprimés.

### Ajouter votre propre modèle

**Ajouter votre propre modèle...** vous permet de choisir un fichier `.gguf`. L'hôte crée sa propre copie dans le dossier des modèles. Votre original n'est jamais modifié ni déplacé. La ligne porte la mention **Ajouté par vous**, et l'hôte ne peut pas estimer la mémoire nécessaire : essayez-le pour voir. **Supprimer** sur un tel modèle n'efface que la copie de l'hôte.

### Libérer de l'espace

**Libérer de l'espace** liste les restes créés par l'application elle-même : téléchargements inachevés qui ne peuvent pas reprendre, anciennes copies laissées après le déplacement de vos modèles, et modèles qu'une vérification a jugés endommagés. Vous confirmez avant toute suppression. Les modèles que vous utilisez et les fichiers que vous avez ajoutés vous-même ne sont jamais listés. Les téléchargements inachevés que vous pouvez encore reprendre sont conservés une semaine.

### Dossier des modèles et déplacement de vos modèles

**Paramètres > Dossier des modèles** définit l'emplacement des nouveaux téléchargements. Laissez le champ vide pour le dossier par défaut, ou saisissez un chemin complet comme `D:\Modèles` puis appuyez sur Entrée. Les modèles déjà téléchargés ne sont ni déplacés ni supprimés ; ils restent à leur emplacement et restent listés. Pour les déplacer, acceptez **Déplacer mes modèles ici**. Ils sont copiés, vérifiés, et seulement ensuite les anciennes copies sont supprimées. Le modèle en cours d'utilisation est déchargé pendant l'opération, et **Annuler le déplacement** laisse tout à sa place.

### Budget mémoire et barres

Les barres **Budget mémoire** de la page Modèles indiquent la mémoire vive et la mémoire graphique : ce qu'utilisent les modèles chargés, ce qui est réservé pendant le chargement d'un modèle, et ce qui reste libre par rapport à vos limites (voir section 7). Un chargement qui dépasserait votre limite de mémoire est refusé avec une raison claire, avant que quoi que ce soit ne soit arrêté.

[screenshot: les barres Budget mémoire avec deux modèles chargés]

### Emplacement : carte graphique, mémoire ou processeur uniquement

L'endroit où un modèle s'exécute est défini par **Paramètres > Performance > Exécuter sur** (Automatique, Carte graphique, Processeur). Un modèle qui ne tient pas sur la carte graphique s'exécute en partie sur le processeur (plus lentement). Voir aussi l'emplacement par modèle dans la section 11, qui inclut **Processeur uniquement**.

### Limites et gratuit ou payant

- Tous les modèles de la liste sont gratuits pour un usage commercial (licences Apache 2.0 ou MIT) ; chaque modèle affiche sa propre licence.
- Les modèles sont téléchargés depuis les pages de leurs éditeurs sur Hugging Face. L'hôte n'accepte qu'une liste de modèles de confiance et signée. S'il ne peut pas charger cette liste, une barre indique **Liste des modèles indisponible** ; les modèles déjà présents sur ce PC continuent de fonctionner.

### Dépannage

- *Aucun modèle à télécharger n'apparaît :* vérifiez votre connexion Internet et appuyez sur **Réessayer** dans l'accueil.
- *Le téléchargement s'arrête :* appuyez sur **Reprendre** ; si l'espace libre manque, libérez de la place ou changez de dossier.
- *Modèle « peut-être trop volumineux » :* choisissez-en un plus petit, ou réduisez la charge dans Paramètres > Performance.

---

## 5. Applications et autorisations

Ouvrez **Appareils et applications** (Ctrl+3).

- **En attente de votre réponse :** une application qui a demandé à utiliser l'IA apparaît ici et sous forme d'invite (« Autoriser Voice Portal à utiliser l'IA sur ce PC ? »). Choisissez **Autoriser**, **Ne pas autoriser** ou **Décider plus tard**. L'invite indique si l'application est une application Portal vérifiée, une application installée qui n'est pas une application Portal, ou une application non vérifiée (elle indique alors d'où elle est lancée ; n'autorisez-la que si vous l'avez lancée vous-même).
- **Applications pouvant utiliser votre hôte d'IA :** chaque application autorisée affiche ses requêtes du jour et la date de sa dernière utilisation.
  - **Pause** refuse les nouvelles requêtes jusqu'à ce que vous reprenez ; l'application conserve son accès.
  - **Priorité** (Haute, Normale, Basse) détermine qui passe en premier lorsque plusieurs applications demandent en même temps. Les requêtes interactives passent avant les travaux par lots, et une requête qui attend longtemps remonte afin qu'une application à basse priorité ne soit jamais privée de réponse.
  - **Retirer l'accès** arrête l'application jusqu'à ce que vous la réautorisiez. **Annuler** apparaît un instant.

Les applications doivent demander avant de pouvoir utiliser l'hôte. L'hôte n'écoute que sur ce PC, sauf si vous activez le partage avec d'autres appareils (section 9).

[screenshot: la page Appareils et applications avec une application en attente et une application autorisée]

Développeurs : l'hôte propose une API locale compatible OpenAI à l'adresse `http://127.0.0.1:41870` (complétions de chat avec streaming, embeddings, liste des modèles). Voir `api-v1.md`.

**Emplacement de stockage :** les applications autorisées et leurs jetons sont conservés dans le dossier de données de l'hôte, protégés pour votre utilisateur Windows. Retirer l'accès d'une application supprime son jeton.

---

## 6. Essayer

**Essayer** (Ctrl+4) vérifie que le modèle fonctionne. Saisissez une courte question et appuyez sur **Demander** (ou Ctrl+Entrée). **Arrêter** interrompt la réponse, **Effacer** vide la page. Des questions suggérées sont proposées. La page indique le temps nécessaire pour les premiers mots et la vitesse en jetons par seconde. Rien de ce que vous saisissez ici n'est enregistré.

[screenshot: Essayer avec une réponse et la ligne de durée]

---

## 7. Réglages de performance

**Paramètres > Performance** évite que l'IA ne ralentisse votre PC. Les modifications s'appliquent immédiatement ; un modèle qui ne tient plus est rechargé avec les nouvelles limites ou déchargé (il reste sur le disque). **Rétablir les valeurs par défaut** restaure la section.

| Réglage | Ce qu'il fait | Par défaut |
|---|---|---|
| Performance | Un préréglage : **Silencieux** (environ la moitié de la vitesse, le PC reste réactif), **Équilibré** (bonne vitesse, vos programmes passent en premier), **Rapide** (réponses les plus rapides, d'autres programmes peuvent ralentir), **Personnalisé** | Équilibré |
| Mémoire maximale pour l'IA | L'IA n'utilise jamais plus de mémoire que cette valeur | 80 % de la mémoire du PC |
| Toujours laisser libre | Mémoire conservée libre pour vos autres programmes | Défini par le préréglage |
| Exécuter sur | **Automatique** (carte graphique lorsqu'elle aide), **Carte graphique** (le plus rapide, si le modèle tient), **Processeur** (plus lent, mais libère la carte graphique pour les jeux) | Automatique |
| Mémoire graphique pour l'IA | Une limite de la mémoire graphique que l'IA peut utiliser | Défini par le préréglage |
| Cœurs du processeur | Moins de cœurs gardent votre PC réactif | Défini par le préréglage |
| Priorité | **Basse** (votre travail d'abord) ou **Normale** | Basse |
| Utilisation maximale du processeur | Un plafond d'utilisation du processeur | Aucune limite (100 %) |
| Ralentir lorsque | L'IA s'exécute avec la priorité la plus basse, utilise au plus la moitié de votre processeur, puis revient à la normale à la fin. Cochez une ou plusieurs options : un jeu ou un programme plein écran est lancé, le PC fonctionne sur batterie, une application Portal est occupée par un travail en direct, comme la dictée | Programme plein écran, batterie et application occupée activés |

Sous **Libérer la mémoire en cas d'inactivité** dans **Paramètres > Général**, vous choisissez **Garder le modèle chargé**, **Après 5 minutes** ou **Après 10 minutes** (10 minutes par défaut, 5 sur batterie). Le modèle se recharge la prochaine fois qu'une application le demande. Le garder chargé répond plus vite, mais utilise de la mémoire en permanence.

Les indications des curseurs précisent si la mémoire choisie permet de faire tenir un modèle entièrement sur la carte graphique, en partie sur le processeur, ou uniquement sur le processeur, et proposent un modèle plus petit lorsque le modèle actuel ne tient plus.

[screenshot: Paramètres > Performance]

**Dépannage :** des réponses lentes sur un PC avec carte graphique signifient généralement que le modèle ne tient que partiellement sur la carte. Choisissez un modèle plus petit ou augmentez la Mémoire graphique pour l'IA. Si votre PC saccade pendant l'utilisation, choisissez Silencieux ou réduisez l'Utilisation maximale du processeur.

---

## 8. Notifications

**Paramètres > Notifications** gère les courts messages Windows affichés lorsque quelque chose requiert votre attention et que la fenêtre est fermée. Ils restent silencieux lorsqu'un programme plein écran est au premier plan, et ne se répètent jamais plus d'une fois toutes les quelques minutes.

| Interrupteur | Signification |
|---|---|
| Un téléchargement de modèle se termine ou échoue | Activé par défaut |
| Une application demande une autorisation | Activé par défaut |
| Le moteur d'IA s'arrête ou une vérification échoue | Activé par défaut |
| Un appareil s'associe ou se connecte depuis l'extérieur | Activé par défaut (les alertes de la section 10) |

Chacun peut être désactivé séparément. **Rétablir les valeurs par défaut** de la même page les restaure.

---

## 9. Permettre à d'autres appareils d'utiliser ce PC

**Paramètres > Autres appareils.** Désactivé par défaut. Lorsqu'il est activé, votre propre ordinateur portable ou un autre PC de votre réseau domestique peut demander à utiliser l'IA de ce PC. Un appareil doit d'abord être associé avec un code, et chaque application qu'il contient nécessite toujours votre autorisation.

### Associer un appareil

1. Activez **Permettre aux autres appareils d'utiliser l'IA de ce PC**. Windows peut demander d'autoriser une règle réseau.
2. Appuyez sur **Associer un appareil**. Un code et un code QR apparaissent, et **Copier le lien** copie un lien d'association.
3. Sur l'autre appareil, ouvrez son application Portal (ou Portal AI Host en mode client, section 12), choisissez de vous connecter à un PC, puis scannez le code, saisissez-le ou collez le lien.
4. Il vérifie qu'il parle bien à ce PC avant d'envoyer quoi que ce soit, puis affiche le nom du PC et une empreinte à confirmer.

Le code ne fonctionne qu'une fois, pendant 5 minutes, pour un seul appareil. Les mauvais essais l'épuisent ; créez-en un nouveau avec **Associer un appareil**. **Annuler l'association** le met fin.

[screenshot: le code d'association et le code QR]

### Appareils associés

Chaque appareil associé est listé avec sa date de dernière utilisation et un bouton **Retirer**. Retirer un appareil coupe immédiatement son accès, et ses requêtes en cours se terminent. Il doit être associé à nouveau pour revenir. **16** appareils au maximum peuvent être associés.

### Autres réglages de cette carte

- **Atteindre aussi ce PC depuis l'extérieur** (le relais), voir la section 13. Par défaut : activé une fois les autres appareils autorisés.
- **Clé d'accès au relais**, voir la section 13. Par défaut : désactivée.
- **Les appareils peuvent remplacer des modèles**, voir section 11. Par défaut : désactivé.

**Ce qui transite par le réseau :** les requêtes et les réponses voyagent chiffrées entre vos propres appareils. La connexion du réseau domestique utilise un certificat que l'appareil a épinglé lors de l'association. Rien ne part vers un serveur de notre part, sauf, en option, le relais aveugle (section 13).

**Dépannage :** *Un appareil ne trouve pas ce PC :* vérifiez que les deux sont sur le même réseau, que ce PC est éveillé et que l'hôte est en cours d'exécution. *Le code est « incorrect » ou « expiré » :* créez-en un nouveau. *« Pas sur votre réseau domestique » :* associez d'abord l'appareil à la maison.

---

## 10. Appareils sur ce PC

La page **Appareils de ce PC** affiche une carte par ordinateur portable associé. C'est l'endroit où voir ce que fait chaque ordinateur portable et le limiter.

[screenshot: Appareils de ce PC avec deux cartes d'appareils]

### Ce qu'affiche une carte

- Statut (En ligne, Inactif, Hors ligne), route (Réseau domestique ou Relais (à distance)) et dernière activité.
- Ce qui s'exécute actuellement et ce qui attend.
- Les requêtes et jetons du jour, avec une barre sur 7 jours, par application.
- Le modèle utilisé, qui l'a chargé, ainsi que la vitesse en jetons par seconde et le délai jusqu'au premier octet.

### Commandes

| Commande | Ce qu'elle fait |
|---|---|
| Pause / Reprendre | Met en pause tout l'appareil, ou l'une de ses applications. L'ordinateur portable reçoit une réponse claire « en pause » et utilise son propre mode de secours, si vous en avez défini un. |
| Renommer | Modifie le nom affiché pour l'appareil |
| Limites | Priorité, limites quotidiennes, heures de silence et modèles autorisés (ci-dessous) |
| Contrôle des modèles (interrupteur) | Permet à cet appareil de charger et décharger des modèles. Voir la section 11 |
| Reprendre | Décharge le modèle chargé par cet appareil. Le choix de ce PC revient. Aucun fichier de modèle n'est touché. |
| Oublier | Retire l'appareil. Son accès cesse immédiatement et son historique d'utilisation est supprimé. Les modèles ne sont pas touchés. |

**Limites** (sous le bouton **Limites** de la carte, puis **Enregistrer**) :

- **Même priorité que les applications de ce PC.** Désactivé (par défaut) : les applications propres à ce PC passent en premier. Activé : l'appareil attend dans la même file.
- **Requêtes par jour** et **Jetons par jour.** Par défaut : aucune limite. Une fois la limite atteinte, l'appareil est invité à revenir après minuit (heure de ce PC).
- **Heures de silence**, avec **De** et **À** (heure de ce PC). L'appareil ne reçoit aucune réponse pendant ces heures. Par défaut : désactivé.
- **Modèles que cet appareil peut utiliser.** Par défaut : tous les modèles.

### Mode jeu

En haut de la page. **Par défaut : désactivé.** Lorsqu'il est activé, les requêtes des autres appareils reçoivent la réponse que ce PC est occupé pendant que vous jouez, afin que les jeux conservent la carte graphique. Ce PC est considéré comme « en jeu » lorsque Windows signale un jeu en plein écran, ou lorsqu'un autre programme maintient le moteur 3D de la carte graphique à 40 % ou plus pendant environ 10 secondes ; les jeux en fenêtre ou en fenêtre sans bordure comptent donc aussi. Ce statut cesse environ 30 secondes après la fin de la charge. Si les compteurs graphiques ne sont pas disponibles sur votre PC, seule la détection du plein écran est utilisée. L'ordinateur portable se rabat alors sur ses propres réglages.

### Alertes

Une notification Windows apparaît lorsqu'un nouvel appareil s'associe, et lorsqu'un appareil se connecte via le relais depuis l'extérieur. Désactivez-les avec **Paramètres > Notifications > Un appareil s'associe ou se connecte depuis l'extérieur**.

### Données

Les chiffres d'utilisation ne sont que des comptages (requêtes et jetons par application et par jour, 7 jours conservés). Aucun texte de question ou de réponse n'est stocké. **Oublier** supprime le fichier d'utilisation de cet appareil, et rien d'autre.

---

## 11. Contrôle des modèles et deux modèles à la fois

Par défaut, un ordinateur portable associé utilise le modèle que ce PC a chargé. Vous pouvez donner davantage à un appareil.

### Contrôle des modèles

**Paramètres > Autres appareils**, sur la ligne de l'appareil : **Contrôle des modèles** (par défaut : désactivé). Une fois activé, l'ordinateur portable peut :
- charger et décharger les modèles installés sur ce PC,
- choisir où un modèle s'exécute (Automatique, Carte graphique, Mémoire système ou Processeur uniquement),
- choisir quels modèles de ce PC chacune de ses propres applications utilise.

Il ne peut jamais télécharger, supprimer, vérifier ou importer des modèles, modifier les paramètres ou toucher à vos fichiers, avec ou sans cette autorisation. Si vous le désactivez, le prochain appel de l'appareil est refusé. Retirer l'appareil supprime l'autorisation, et l'associer à nouveau ne la rétablit pas.

Le PC indique qui a chargé quoi : « Chargé par <appareil> : <modèle> (carte graphique) ». **Reprendre** (sur la carte et dans les Paramètres) décharge le modèle chargé par un appareil. Le choix de ce PC revient, et le modèle se charge à la prochaine requête.

### Deux modèles à la fois

Avec le contrôle des modèles, un ordinateur portable peut garder un second modèle chargé sur ce PC, par exemple un sur la carte graphique et un dans la mémoire système.
- Votre propre modèle reste dans le premier emplacement. Le modèle d'un appareil va dans le second, de sorte qu'un appareil ne remplace jamais votre modèle juste pour obtenir le sien.
- Chaque emplacement a sa propre file d'attente, de sorte que deux modèles peuvent répondre en même temps. Une requête qui nomme le second modèle lui est envoyée ; tout le reste va à votre propre modèle.
- Avant le chargement, l'hôte compte ce que l'autre emplacement utilise déjà par rapport à vos limites. Un second modèle qui ne tient pas est refusé (« mémoire insuffisante »). Rien n'est arrêté silencieusement.
- Vos choix passent toujours en premier. Si vous choisissez un autre modèle, en déchargez un, ou si votre propre modèle a besoin de la mémoire, le modèle de l'appareil est arrêté et l'appareil en est informé.
- Le second modèle d'un appareil n'est jamais gardé chargé indéfiniment ; il se décharge comme les autres lorsqu'il est inactif.
- **Processeur uniquement** exécute un modèle sans couches graphiques, en utilisant environ la moitié du processeur, par exemple un modèle d'appoint sur le processeur pendant qu'un autre utilise la carte graphique.

[screenshot: la page Modèles avec deux modèles chargés par des propriétaires différents]

### Les appareils peuvent remplacer des modèles

**Paramètres > Autres appareils > Les appareils peuvent remplacer des modèles** (par défaut : désactivé). Désactivé : un appareil ne peut pas décharger un modèle que vos propres applications utilisent. Activé : il le peut, et vos applications rechargent leur modèle à leur prochaine requête. Avec deux appareils, le premier à charger garde son modèle.

---

## 12. Utiliser votre PC domestique depuis un ordinateur portable (mode client)

Le mode client permet à Portal AI Host sur un ordinateur portable d'utiliser l'IA d'un autre de vos PC au lieu d'exécuter un modèle lui-même. Les applications de l'ordinateur portable fonctionnent comme d'habitude.

### Configuration

1. Sur le PC domestique, activez **Permettre aux autres appareils d'utiliser l'IA de ce PC** et appuyez sur **Associer un appareil** (section 9). Appuyez sur **Copier le lien** et envoyez le lien à l'ordinateur portable.
2. Sur l'ordinateur portable, ouvrez **Paramètres > PC domestique** (ou choisissez **Utiliser mon PC domestique** dans la fenêtre d'accueil).
3. Dans **Où s'exécute l'IA**, choisissez **Utiliser mon PC domestique**. Une fenêtre **Utiliser mon PC domestique** s'ouvre. Collez le lien avec **Coller le lien**, appuyez sur **Suivant**, vérifiez le nom du PC et l'empreinte, puis **Associer**. Nommez l'ordinateur portable.
4. L'accueil affiche désormais le nom du PC et un statut : **Connecté - réseau domestique**, **Connecté - via relais**, **Connexion en cours...** ou **Injoignable**.

[screenshot: la fenêtre d'association sur l'ordinateur portable]

### Réglages (Paramètres > PC domestique)

| Réglage | Choix | Par défaut |
|---|---|---|
| Où s'exécute l'IA | Ce PC / Utiliser mon PC domestique | Ce PC |
| Si le PC domestique est injoignable | **Aucun** (les applications utilisent leur IA intégrée) / **Utiliser le modèle de cet ordinateur portable** (charge à la demande) | Aucun |
| Connexion hors de chez vous | **Automatique** / **Manuelle** (un interrupteur **Utiliser le relais** s'applique alors à cette session seulement et se réinitialise à la fermeture de l'application) | Automatique |

- La première fois que le PC domestique est injoignable, on vous demande une fois quoi faire ; vous pouvez le modifier ici à tout moment.
- **Associer le PC** associe à nouveau ; **Dissocier** oublie le PC (les modèles et téléchargements restent tels quels) ; **Changer de PC** (sur l'accueil) ouvre ces réglages ; **Exécuter l'IA ici** (sur l'accueil) quitte le mode client et conserve l'association enregistrée.
- Avec le contrôle des modèles accordé par le PC domestique, **Modèles sur <nom du PC>** apparaît avec des boutons Charger, Décharger et placement, ainsi que **Modèle du PC pour chaque application**. Sans cette autorisation, la page indique : « Demandez au propriétaire de <nom du PC> d'activer le contrôle des modèles pour cet ordinateur portable ».
- Un ordinateur portable en mode client n'accepte pas les connexions des autres appareils et ne peut pas servir de relais vers votre PC domestique.

### Ce qu'il advient des modèles propres à cet ordinateur portable

En entrant en mode client, le modèle chargé sur l'ordinateur portable est déchargé pour libérer de la mémoire. Les fichiers de modèles, les téléchargements et le dossier des modèles ne sont pas touchés. En quittant le mode client, le modèle se recharge lorsqu'on le demande.

### Dépannage

- **Injoignable :** vérifiez que le PC domestique est allumé et sur le même réseau, ou activez le relais.
- **« Cet ordinateur portable a été retiré du PC » :** le PC domestique l'a supprimé. Choisissez **Associer à nouveau**.
- **« L'identité de <PC> a changé » :** le certificat du PC ne correspond plus à celui enregistré par l'ordinateur portable. Si vous ne vous y attendiez pas, n'associez pas à nouveau. Sinon, dissociez, puis associez à nouveau.
- **L'association depuis l'extérieur échoue :** associez d'abord à la maison ; le lien d'association doit pointer vers une adresse de votre réseau domestique.
- **Pause, heures de silence, jeu en cours ou limite quotidienne atteinte :** le PC indique à l'ordinateur portable la raison ; celui-ci utilise son mode de secours.

**Emplacement de stockage :** l'ordinateur portable conserve son association dans le dossier de données de l'hôte, sous `client\`. La clé de l'appareil et les jetons sont protégés pour votre utilisateur Windows. **Dissocier** les supprime. Les sauvegardes de ce dossier ne peuvent pas être restaurées sous un autre utilisateur Windows.

---

## 13. Hors de chez vous : le relais et la clé d'accès au relais

Le relais permet à un appareil associé d'atteindre votre PC depuis n'importe où. **Paramètres > Autres appareils > Atteindre aussi ce PC depuis l'extérieur.**

- Votre PC se connecte vers l'extérieur à un petit service de relais. Le relais ne transmet que des octets brouillés qu'il ne peut pas lire, et il ne conserve aucun contenu. Chaque appareil doit tout de même être associé et signe chaque requête.
- La ligne d'état indique **Accessible hors de chez vous**, **Réseau domestique uniquement**, **Connexion au relais...** ou **Impossible de joindre le relais**.
- Il fonctionne sur le port web habituel, de sorte que la plupart des réseaux d'entreprise l'autorisent.
- **Appareils professionnels :** le contenu professionnel transitera par votre PC personnel. Vérifiez d'abord les règles de votre employeur.

### Clé d'accès au relais

**Paramètres > Autres appareils > Clé d'accès au relais** (par défaut : désactivée). Lorsqu'elle est activée, seuls les appareils associés avec la clé incluse dans le code d'association peuvent atteindre ce PC via le relais, ce qui empêche des inconnus d'utiliser les places du relais réservées à votre PC.

- Activez-la **avant d'associer** un appareil. Les appareils associés auparavant doivent s'associer à nouveau pour utiliser le relais (le réseau domestique continue de fonctionner).
- **Nouvelle clé** crée une clé neuve après confirmation. Chaque appareil associé doit alors s'associer à nouveau pour utiliser le relais ; à la maison, rien ne change. Retirer un appareil ne modifie pas la clé, utilisez donc **Nouvelle clé** après avoir retiré un appareil auquel vous ne faites pas confiance.
- Si le fichier de clé ne peut pas être utilisé, le relais reste désactivé plutôt qu'ouvert, et le réseau domestique continue de fonctionner. Un fichier de clé endommagé est mis de côté sous un nom en `.bad` et n'est jamais écrasé.
- Si l'interrupteur ne reste pas activé, le service de relais doit peut-être être mis à jour de son côté. Désactivez-le à nouveau.

**Limites :** le relais limite la vitesse et la quantité qu'un appareil peut envoyer, afin qu'un seul appareil ne sature pas le service. Une utilisation normale reste très en dessous de ces limites.

---

## 14. Confidentialité et rapports d'erreur

- **Aucune statistique ni suivi d'utilisation.** Seul un rapport d'erreur peut quitter votre PC, et uniquement si vous l'avez laissé activé.
- **Votre texte reste sur vos appareils.** Les requêtes et les réponses ne sont jamais enregistrées ni journalisées par l'hôte. Les journaux ne contiennent que des comptages, des durées et des états.
- **Les rapports d'erreur sont activés par défaut, et vous pouvez les désactiver.** Si la fenêtre ou l'hôte en arrière-plan plante, un court rapport nous aide à corriger le problème. Désactivez-le à tout moment dans **Paramètres > Confidentialité > Envoyer les rapports d'erreur** ; c'est immédiat. La fenêtre d'accueil propose le même choix, coché par défaut, avant tout envoi.
  - **Envoyé :** le type d'erreur et l'endroit où elle s'est produite (noms de fonctions et de modules), les versions de l'application et de Windows, s'il s'agit d'une version du Store, et si l'erreur provient de la fenêtre ou de l'hôte.
  - **Jamais envoyé :** les requêtes ou les réponses, tout ce que vous saisissez, les noms de fichiers ou les chemins de dossiers, le nom de votre ordinateur ou de votre utilisateur, les applications qui utilisent l'hôte, ou votre adresse IP. Le texte de l'erreur est supprimé.
  - **Conservés 90 jours**, puis supprimés.
  - **Ce qui est envoyé** (Paramètres > Confidentialité) ouvre la page de confidentialité.
- **Tout est chiffré** lorsque cela quitte votre PC vers un autre de vos appareils.
- **Politique de confidentialité :** À propos > **Politique de confidentialité**.
- Questions, ou demande de suppression d'un rapport : mindthaapp@gmail.com.

[screenshot: Paramètres > Confidentialité avec l'interrupteur des rapports d'erreur]

---

## 15. Où sont stockées vos données, sauvegarde et suppression

Tout se trouve dans le dossier de données propre à l'application. **À propos > Ouvrir le dossier de données** l'ouvre.

| Élément | Emplacement (dans le dossier de données) | Sauvegarder ? |
|---|---|---|
| Modèles téléchargés | `models\`, ou `Portal AI Host Models\` dans le dossier choisi dans les Paramètres | Facultatif. Ils peuvent être téléchargés à nouveau. Ils sont volumineux. |
| Paramètres | `settings.json` | Oui |
| Applications autorisées et leurs jetons | Le magasin d'applications de l'hôte | Non transportable (protégé pour votre utilisateur Windows) |
| Appareils associés, limites, statistiques d'utilisation | Le magasin d'appareils de l'hôte et un petit fichier de statistiques par appareil | Non transportable |
| Association de l'ordinateur portable (mode client) | `client\` | Non transportable. Associez à nouveau sur un nouveau PC. |
| Journaux | `logs\` (rotatifs, limités à 10 Mo) | Non |
| Fichier de découverte pour les applications | `discovery\host.json` | Non |

**Sauvegarder :** fermez Portal AI Host, puis copiez le dossier de données. Les paramètres et les modèles peuvent être restaurés en les recopiant. Les autorisations des applications, les associations d'appareils et les jetons sont protégés pour votre utilisateur Windows et votre PC ; sur un nouveau PC, vous autorisez donc à nouveau les applications et associez à nouveau les appareils.

**Supprimer :**
- *Un modèle :* **Modèles > Supprimer**. Seuls les fichiers téléchargés par l'hôte sont effacés.
- *Les restes :* **Modèles > Libérer de l'espace**.
- *Un appareil :* **Oublier** (Appareils de ce PC) ou **Retirer** (Paramètres).
- *L'accès d'une application :* **Retirer l'accès** (Appareils et applications).
- *Tout :* désinstallez l'application. Un dossier de modèles personnalisé que vous avez choisi reste en place ; supprimez vous-même son dossier `Portal AI Host Models` si vous voulez qu'il disparaisse.

L'application ne supprime jamais ce qu'elle n'a pas créé, n'écrase jamais vos fichiers, et un modèle que vous avez ajouté vous-même n'est retiré qu'en tant que copie propre à l'hôte (votre fichier original reste intact).

---

## 16. Raccourcis clavier

| Touches | Action |
|---|---|
| Ctrl+1 | Accueil |
| Ctrl+2 | Modèles |
| Ctrl+3 | Appareils et applications |
| Ctrl+4 | Essayer |
| Ctrl+, | Paramètres |
| F1 | À propos |
| Ctrl+Entrée | Demander, dans Essayer |
| Échap | Fermer Nouveautés ou une boîte de dialogue |

La liste se trouve aussi dans le menu de l'application > **Raccourcis clavier**.

Apparence : **Paramètres > Apparence** propose Thème (par défaut : votre réglage Windows), Matériau de la fenêtre (Mica), Coins (Arrondis), Densité (Standard), Taille du texte (Normale) et Animations (Suivre Windows). Le contraste élevé de Windows utilise toujours des couleurs système unies.

---

## 17. Dépannage et FAQ

**L'hôte indique qu'il ne fonctionne pas.** Appuyez sur **Démarrer Portal AI Host** dans l'accueil. S'il ne peut pas démarrer, redémarrez le PC ; si cela échoue encore, réinstallez depuis le Store.

**« Un autre utilisateur Windows de ce PC exécute déjà Portal AI Host. »** Demandez à cet utilisateur de le quitter, ou connectez-vous en tant que lui.

**Les réponses sont lentes.** Voir la section 7. Un modèle trop grand pour votre mémoire graphique s'exécute en partie sur le processeur.

**Une application indique qu'aucune IA n'est disponible.** Vérifiez que l'application est autorisée (Appareils et applications), qu'elle n'est pas en pause, et qu'un modèle est installé et n'est pas trop volumineux pour le PC.

**La première réponse est lente, les suivantes sont rapides.** Le modèle était en veille. Il se charge à la première requête. Choisissez **Garder le modèle chargé** dans Paramètres > Général pour éviter cela.

**Mes données sont-elles envoyées quelque part ?** Non. Les requêtes et les réponses restent sur vos appareils. Seuls les rapports d'erreur peuvent sortir (section 14), et vous pouvez les désactiver.

**Puis-je l'utiliser sans Internet ?** Oui, une fois un modèle téléchargé.

**Est-ce payant ?** Non.

**Quel modèle choisir ?** Le modèle recommandé. Choisissez-en un plus petit si votre PC semble lent, un plus grand si vous voulez de meilleures réponses et que vous disposez de la mémoire.

**Le téléchargement est bloqué sur « Vérification du téléchargement ».** Patientez. Les gros fichiers prennent une minute à vérifier.

**L'association indique « trop d'appareils ».** La limite est de 16 appareils. Retirez-en un.

**Où est l'icône de la zone de notification ?** Cliquez sur la flèche près de l'horloge pour afficher les icônes masquées.

Si vous êtes toujours bloqué, utilisez **À propos > Copier la version**, puis écrivez à mindthaapp@gmail.com et collez la version dans votre message.

---

## 18. À venir

Ces éléments sont prévus et ne sont pas encore dans l'application.

- **Catégories de modèles sur la page Modèles :** regroupés par tâche (chat et rédaction, programmation, raisonnement, vision, embeddings, parole), avec filtres et recherche.
- **Génération d'images et de vidéos** sur ce PC, avec un « réalisateur » qui enchaîne des plans en une vidéo. Les modèles vidéo apparaîtront comme « Bientôt disponible » tant que le moteur n'existe pas.
- **Choix du modèle qui répond**, et modèles qui se demandent mutuellement de l'aide, dans AI Portal.
- **Version macOS.**

---

## 19. Assistance

- **Commentaires et idées :** À propos > **Envoyer un commentaire ou suggérer une idée**.
- **Questions d'assistance et de confidentialité :** mindthaapp@gmail.com.
- **Version :** À propos l'affiche, et **Copier la version** la place dans le presse-papiers pour votre message.
- **Licences :** Portal AI Host inclut llama.cpp (licence MIT), qui fait tourner les modèles sur votre PC. Les modèles sont téléchargés sous leurs propres licences (Apache 2.0 ou MIT). Voir À propos.
