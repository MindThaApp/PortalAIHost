# Fiche Store (français, fr-fr)

Espaces réservés et notes : collez ces textes dans Partner Center > la soumission > Fiches Store > Français. Ils traduisent listing-en.md et doivent être relus par un locuteur natif avant la soumission. Les notes de certification, la classification par âge et la justification des capacités restent en anglais (uniquement dans listing-en.md).

## Nom de l'application

Portal AI Host

## Description courte

Faites tourner un modèle d'IA local sur votre propre PC et partagez-le avec vos applications Portal. Un clic choisit le meilleur modèle pour votre matériel, le télécharge et le garde prêt. Votre texte reste sur ce PC : ni compte, ni cloud, ni abonnement.

## Description

Portal AI Host donne à votre PC son propre modèle d'IA et laisse vos autres applications l'utiliser.

Au lieu que chaque application télécharge son propre modèle ou envoie votre texte à un service cloud, Portal AI Host garde un seul modèle sur votre PC et le partage avec les applications que vous autorisez. Voice Portal et les autres applications Portal peuvent alors rédiger des résumés, des comptes rendus et des réponses avec un modèle plus grand et plus performant que ce qu'elles pourraient embarquer seules.

Simple à configurer
- Au premier lancement, l'application analyse votre PC (mémoire, carte graphique, espace disque libre) et recommande le meilleur modèle qui y fonctionne bien, avec une explication claire.
- Un clic le télécharge. Les téléchargements peuvent être mis en pause et repris, et chaque fichier est vérifié avant d'être utilisé.
- Rien d'autre à installer : le moteur est intégré. Pas de Python, pas de ligne de commande, pas d'outils supplémentaires.

Privé par conception
- Le modèle s'exécute sur ce PC. Les requêtes et les réponses ne le quittent jamais.
- Aucun compte et aucune connexion.
- Aucune analyse d'usage. Les rapports de plantage facultatifs ne contiennent jamais vos fichiers ni ce que vous saisissez, et vous pouvez les désactiver. Vos requêtes ne sont jamais enregistrées ni journalisées.
- L'hôte n'écoute que sur ce PC (boucle locale). Les autres ordinateurs de votre réseau ne peuvent pas l'atteindre.

Vous gardez le contrôle
- Une application doit demander avant de pouvoir utiliser l'hôte, et vous décidez : autoriser, refuser ou décider plus tard. Vous pouvez retirer l'accès d'une application à tout moment.
- L'hôte indique si une application est vérifiée (son éditeur) ou non, pour que vous sachiez qui demande.
- Choisissez où les modèles sont stockés et supprimez ceux dont vous n'avez plus besoin. L'hôte ne supprime que les fichiers qu'il a lui-même téléchargés.
- Le démarrage avec Windows est facultatif et désactivé tant que vous ne l'activez pas.

Adapté à votre matériel
- Utilise votre carte graphique via Vulkan (AMD, NVIDIA et Intel) et bascule sur le processeur quand aucun GPU adapté n'est disponible.
- Libère la mémoire quand le modèle est inactif, ou le garde chargé si vous préférez.

Pour les développeurs
- L'hôte propose une API standard compatible OpenAI sur 127.0.0.1 (complétions de chat avec streaming, embeddings et liste des modèles), de sorte que les bibliothèques clientes existantes fonctionnent. Chaque application a d'abord besoin de l'accord de l'utilisateur.

Les modèles sont téléchargés depuis les pages de leurs éditeurs sur Hugging Face. Chaque modèle a sa propre licence ; l'application ne propose que des modèles dont la licence autorise l'usage commercial (Apache 2.0 ou MIT).

## Fonctionnalités (20 au maximum, 200 caractères chacune)

1. Exécute un modèle d'IA en local sur votre PC, sans cloud
2. Un seul modèle partagé par toutes vos applications Portal
3. Recommande le meilleur modèle pour votre matériel
4. Téléchargement en un clic, avec pause, reprise et vérification des fichiers
5. Utilise votre carte graphique (Vulkan) avec repli automatique sur le processeur
6. Ni compte, ni connexion, ni abonnement
7. Aucune analyse d'usage ; rapports de plantage facultatifs et nettoyés ; requêtes jamais enregistrées ni journalisées
8. N'écoute que sur ce PC (boucle locale)
9. Les applications doivent demander d'abord ; vous autorisez, refusez ou retirez l'accès
10. Indique si une application qui demande est vérifiée
11. Libère la mémoire en cas d'inactivité, ou garde le modèle chargé
12. Choix du dossier de vos modèles
13. Démarrage facultatif avec Windows, avec icône de notification
14. Zone « Essayer » pour vérifier que le modèle fonctionne
15. API locale compatible OpenAI pour les développeurs
16. Partage équitable quand plusieurs applications demandent en même temps
17. Reprend tout seul si le moteur s'arrête

## Mots-clés (7)

1. IA locale
2. IA hors ligne
3. IA privée
4. LLM
5. modèle d'IA
6. modèle de langage
7. serveur d'IA

## Nouveautés de cette version (1.0)

Première version. Téléchargez un modèle d'IA local en un clic et partagez-le avec vos applications Portal, en privé, sur votre propre PC.

## Configuration requise (texte complémentaire)

> Fonctionne mieux avec 16 Go de mémoire et une carte graphique de 8 Go de mémoire vidéo. Sans carte graphique adaptée, le modèle s'exécute sur le processeur, ce qui est plus lent. Les PC Arm64 exécutent le modèle sur le processeur. Les modèles nécessitent de 2 à 20 Go d'espace disque libre.

## Catégorie

Productivité
