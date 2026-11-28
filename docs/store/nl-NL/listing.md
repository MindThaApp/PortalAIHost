# Store-vermelding (Nederlands, nl-NL)

Vertaling van de Engelse Store-vermelding (listing-en.md). Alleen de voor gebruikers zichtbare onderdelen. Productnamen en links blijven ongewijzigd.

## Appnaam

Portal AI Host

## Korte beschrijving

Draai een lokaal AI-model op je eigen pc en deel het met je Portal-apps. Eén klik kiest het beste model voor je hardware, downloadt het en houdt het klaar. Je tekst blijft op deze pc: geen account, geen cloud, geen abonnement.

## Beschrijving

Portal AI Host geeft je pc een eigen AI-model, en laat je andere apps dat gebruiken.

In plaats van dat elke app een eigen model downloadt, of je tekst naar een clouddienst stuurt, houdt Portal AI Host één model op je pc en deelt het met de apps die je toestaat. Voice Portal en de andere Portal-apps kunnen zo samenvattingen, notulen en antwoorden schrijven met een groter, beter model dan ze zelf zouden kunnen meenemen.

Eenvoudig in te stellen
- Bij de eerste start controleert de app je pc (geheugen, videokaart, vrije schijfruimte) en raadt het beste model aan dat er goed op draait, met een duidelijke uitleg.
- Eén klik downloadt het. Downloads kunnen worden gepauzeerd en hervat, en elk bestand wordt gecontroleerd voordat het wordt gebruikt.
- Er is niets anders te installeren: de engine zit erin. Geen Python, geen opdrachtregel, geen extra hulpmiddelen.

Privé by design
- Het model draait op deze pc. Prompts en antwoorden verlaten nooit je pc.
- Geen account en geen aanmelding.
- Geen analyses. Optionele crashrapporten bevatten nooit je bestanden of wat je typt, en je kunt ze uitzetten. Je prompts worden nooit opgeslagen of gelogd.
- De host luistert alleen op deze pc (loopback). Andere computers in je netwerk kunnen hem niet bereiken.

Jij houdt de regie
- Een app moet om toestemming vragen voordat hij de host kan gebruiken, en jij beslist: toestaan, weigeren of later beslissen. Je kunt de toegang van een app altijd intrekken.
- De host laat zien of een app geverifieerd is (de uitgever), zodat je weet wie er vraagt.
- Kies waar modellen worden opgeslagen en verwijder modellen die je niet meer nodig hebt. De host verwijdert alleen bestanden die hij zelf heeft gedownload.
- Starten met Windows is optioneel en staat uit tot je het aanzet.

Werkt met jouw hardware
- Gebruikt je videokaart via Vulkan (AMD, NVIDIA en Intel), en valt terug op de processor als er geen geschikte GPU is.
- Geeft geheugen vrij wanneer het model inactief is, of houdt het geladen als je dat liever hebt.

Voor ontwikkelaars
- De host biedt een standaard OpenAI-compatibele API op 127.0.0.1 (chat completions met streaming, embeddings en een modellenlijst), zodat bestaande clientbibliotheken werken. Elke app heeft eerst toestemming van de gebruiker nodig.

Modellen worden gedownload van de pagina's van hun uitgevers op Hugging Face. Elk model heeft een eigen licentie; de app biedt alleen modellen aan waarvan de licentie commercieel gebruik toestaat (Apache 2.0 of MIT).

## Functies (Partner Center "Productfuncties", maximaal 20, elk 200 tekens)

1. Draait lokaal een AI-model op je pc, geen cloud nodig
2. Eén model gedeeld door al je Portal-apps
3. Raadt het beste model voor jouw hardware aan
4. Download met één klik, met pauzeren, hervatten en bestandscontrole
5. Gebruikt je videokaart (Vulkan), met automatische terugval op de processor
6. Geen account, geen aanmelding, geen abonnement
7. Geen analyses; crashrapporten zijn optioneel en geanonimiseerd; prompts worden nooit opgeslagen of gelogd
8. Luistert alleen op deze pc (loopback)
9. Apps moeten eerst vragen; jij staat toe, weigert of trekt toegang in
10. Toont of een vragende app geverifieerd is
11. Geeft geheugen vrij als het inactief is, of houdt het model geladen
12. Kies de map voor je modellen
13. Optioneel starten met Windows, met een pictogram in het systeemvak
14. Vak "Probeer het" om te controleren of het model werkt
15. OpenAI-compatibele lokale API voor ontwikkelaars
16. Eerlijke verdeling als meerdere apps tegelijk vragen
17. Herstelt zichzelf als de engine stopt

## Zoektermen (trefwoorden, 7)

1. lokale AI
2. offline AI
3. privé AI
4. LLM
5. AI-model
6. taalmodel
7. AI-server

Geen namen van andere producten als trefwoord (Store-beleid 10.1.3).

## Wat is nieuw in deze versie (1.0)

Eerste versie. Download met één klik een lokaal AI-model en deel het privé met je Portal-apps, op je eigen pc.

## Systeemvereisten (tekst van de vermelding)

> Werkt het best met 16 GB geheugen en een videokaart met 8 GB videogeheugen. Zonder geschikte videokaart draait het model op de processor, wat trager is. Arm64-pc's draaien het model op de processor. Modellen hebben 2 tot 20 GB vrije schijfruimte nodig.

Minimum: Windows 11 (versie 21H2, build 22000) of nieuwer, x64 of Arm64. Geheugen: 8 GB. Vrije schijfruimte: 5 GB (de app zelf is klein; modellen zijn 2 tot 20 GB, grotere optionele modellen tot ongeveer 140 GB).

Aanbevolen: 16 GB geheugen of meer (32 GB of meer voor de grote modellen). Een Vulkan-geschikte videokaart met 8 GB videogeheugen of meer (AMD, NVIDIA of Intel). 25 GB vrije schijfruimte.
