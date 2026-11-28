# Fiche Store (français, fr-FR)

Traduction de la fiche Store anglaise (listing-en.md). Sections destinées aux utilisateurs uniquement. Les noms de produits et les liens restent inchangés.

## Nom de l'application

Portal AI Host

## Description courte

Faites tourner un modèle d'IA local sur votre propre PC et partagez-le avec vos applications Portal. Un clic choisit le meilleur modèle pour votre matériel, le télécharge et le tient prêt. Votre texte reste sur ce PC : pas de compte, pas de cloud, pas d'abonnement.

## Description

Portal AI Host donne à votre PC son propre modèle d'IA et permet à vos autres applications de l'utiliser.

Plutôt que chaque application télécharge son propre modèle ou envoie votre texte vers un service cloud, Portal AI Host conserve un seul modèle sur votre PC et le partage avec les applications que vous autorisez. Voice Portal et les autres applications Portal peuvent ainsi rédiger des résumés, des comptes rendus et des réponses avec un modèle plus grand et plus performant que ce qu'elles pourraient embarquer seules.

Simple à configurer
- Au premier lancement, l'application analyse votre PC (mémoire, carte graphique, espace disque libre) et recommande le meilleur modèle qui y tourne bien, avec une explication claire.
- Un clic suffit pour le télécharger. Les téléchargements peuvent être mis en pause et repris, et chaque fichier est vérifié avant utilisation.
- Rien d'autre à installer : le moteur est intégré. Pas de Python, pas de ligne de commande, pas d'outils supplémentaires.

Privé par conception
- Le modèle tourne sur ce PC. Les requêtes et les réponses ne le quittent jamais.
- Aucun compte, aucune connexion.
- Aucune statistique d'utilisation. Les rapports d'erreur facultatifs ne contiennent jamais vos fichiers ni ce que vous saisissez, et vous pouvez les désactiver. Vos requêtes ne sont jamais enregistrées ni journalisées.
- L'hôte n'écoute que sur ce PC (boucle locale). Les autres ordinateurs de votre réseau ne peuvent pas l'atteindre.

Vous gardez le contrôle
- Une application doit demander l'autorisation avant d'utiliser l'hôte, et c'est vous qui décidez : autoriser, refuser ou décider plus tard. Vous pouvez retirer l'accès d'une application à tout moment.
- L'hôte indique si une application est vérifiée (son éditeur), pour que vous sachiez qui fait la demande.
- Choisissez où sont stockés les modèles et supprimez ceux dont vous n'avez plus besoin. L'hôte ne supprime que les fichiers qu'il a téléchargés lui-même.
- Le démarrage avec Windows est facultatif et désactivé tant que vous ne l'activez pas.

Adapté à votre matériel
- Utilise votre carte graphique via Vulkan (AMD, NVIDIA et Intel), et se rabat sur le processeur lorsqu'aucun GPU adapté n'est disponible.
- Libère la mémoire lorsque le modèle est inactif, ou le garde chargé si vous le préférez.

Pour les développeurs
- L'hôte propose une API standard compatible OpenAI sur 127.0.0.1 (complétions de chat avec streaming, embeddings et liste de modèles), afin que les bibliothèques clientes existantes fonctionnent. Chaque application doit d'abord obtenir l'accord de l'utilisateur.

Les modèles sont téléchargés depuis les pages de leurs éditeurs sur Hugging Face. Chaque modèle a sa propre licence ; l'application ne propose que des modèles dont la licence autorise l'usage commercial (Apache 2.0 ou MIT).

## Fonctionnalités (« Fonctionnalités du produit » dans Partner Center, 20 maximum, 200 caractères chacune)

1. Fait tourner un modèle d'IA localement sur votre PC, sans cloud
2. Un seul modèle partagé par toutes vos applications Portal
3. Recommande le meilleur modèle pour votre matériel
4. Téléchargement en un clic avec pause, reprise et vérification des fichiers
5. Utilise votre carte graphique (Vulkan) avec repli automatique sur le processeur
6. Aucun compte, aucune connexion, aucun abonnement
7. Aucune statistique ; rapports d'erreur facultatifs et épurés ; requêtes jamais enregistrées ni journalisées
8. Écoute uniquement sur ce PC (boucle locale)
9. Les applications doivent demander l'accès ; vous autorisez, refusez ou retirez l'accès
10. Indique si une application demandeuse est vérifiée
11. Libère la mémoire lorsqu'il est inactif, ou garde le modèle chargé
12. Choix du dossier de vos modèles
13. Démarrage facultatif avec Windows, avec une icône dans la zone de notification
14. Zone « Essayer » pour vérifier que le modèle fonctionne
15. API locale compatible OpenAI pour les développeurs
16. Partage équitable lorsque plusieurs applications demandent en même temps
17. Se rétablit automatiquement si le moteur s'arrête

## Mots-clés de recherche (7)

1. IA locale
2. IA hors ligne
3. IA privée
4. LLM
5. modèle d'IA
6. modèle de langage
7. serveur IA

Aucun nom de produit tiers dans les mots-clés (politique du Store 10.1.3).

## Nouveautés de cette version (1.0)

Première version. Téléchargez un modèle d'IA local en un clic et partagez-le en privé avec vos applications Portal, sur votre propre PC.

## Configuration requise (texte de la fiche)

> Fonctionne au mieux avec 16 Go de mémoire et une carte graphique dotée de 8 Go de mémoire vidéo. Sans carte graphique adaptée, le modèle tourne sur le processeur, plus lentement. Les PC Arm64 exécutent le modèle sur le processeur. Les modèles nécessitent entre 2 et 20 Go d'espace disque libre.

Minimum : Windows 11 (version 21H2, build 22000) ou ultérieur, x64 ou Arm64. Mémoire : 8 Go. Espace disque libre : 5 Go (l'application elle-même est petite ; les modèles font de 2 à 20 Go, les modèles optionnels plus grands jusqu'à environ 140 Go).

Recommandé : 16 Go de mémoire ou plus (32 Go ou plus pour les grands modèles). Une carte graphique compatible Vulkan avec 8 Go de mémoire vidéo ou plus (AMD, NVIDIA ou Intel). 25 Go d'espace disque libre.
