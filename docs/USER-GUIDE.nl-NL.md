# Portal AI Host: gebruikershandleiding

Portal AI Host draait een AI-model op je pc en deelt het met je Portal-apps. Je vragen en bestanden blijven op je eigen apparaten. Deze handleiding legt uit wat elk onderdeel van de app doet, wat de instellingen doen en wat je kunt doen als er iets misgaat.

Plekken waar een afbeelding hoort, zijn gemarkeerd met `[screenshot: ...]`.

## Inhoud

1. Snelstart
2. Systeemvereisten, installatie, update, verwijderen
3. Je weg vinden in de app
4. Modellen
5. Apps en machtigingen
6. Probeer het
7. Prestatie-instellingen
8. Meldingen
9. Andere apparaten deze pc laten gebruiken
10. Apparaten op deze pc
11. Modelbeheer en twee modellen tegelijk
12. Je thuis-pc gebruiken vanaf een laptop (clientmodus)
13. Weg van huis: de relay en de relaytoegangssleutel
14. Privacy en crashrapporten
15. Waar je gegevens staan, back-up en verwijderen
16. Sneltoetsen
17. Problemen oplossen en veelgestelde vragen
18. Later beschikbaar
19. Ondersteuning

---

## 1. Snelstart

1. Installeer Portal AI Host uit de Microsoft Store en open de app.
2. Het welkomstvenster controleert je pc (geheugen, videokaart, vrije schijfruimte) en raadt één model aan. Kies **Starten met aanbevolen instellingen**, of **Downloaden** om zelf een model te kiezen. **Later kiezen** slaat deze stap over.
3. Zoek het pictogram naast de klok. Portal AI Host wacht daar rustig. Klik erop voor een snel overzicht.
4. Open **Probeer het** en stel een korte vraag om te controleren of het model werkt.
5. De eerste keer dat een Portal-app AI wil gebruiken, vraagt een melding of je dat toestaat of weigert.

[screenshot: het welkomstvenster met het aanbevolen model]

Als een laptop juist de AI van je thuis-pc moet gebruiken, kies dan **Mijn thuis-pc gebruiken** in het welkomstvenster. Zie hoofdstuk 12.

**Gratis of betaald:** Portal AI Host is gratis. Er is geen account, aanmelding of aankoop nodig voor een functie in deze handleiding.

---

## 2. Systeemvereisten, installatie, update, verwijderen

### Vereisten

| | Minimum | Aanbevolen |
|---|---|---|
| Windows | Windows 11 (21H2, build 22000) of nieuwer, x64 of Arm64 | Hetzelfde |
| Geheugen | 8 GB | 16 GB of meer (32 GB of meer voor de grote modellen) |
| Grafisch | Geen (het model draait dan op de processor, wat trager is) | Een Vulkan-videokaart met 8 GB videogeheugen of meer (AMD, NVIDIA of Intel) |
| Vrije schijfruimte | 5 GB | 25 GB |

Modellen zijn elk 2 tot 20 GB. Arm64-pc's draaien het model op de processor. Een internetverbinding is alleen nodig om modellen te downloaden en voor de relay; zodra een model op je pc staat, werkt het offline.

### Installatie

Installeer de app uit de Microsoft Store. Er is verder niets nodig: geen Python, geen opdrachtregel, geen andere software. De AI-engine maakt deel uit van de app.

### Starten met Windows

**Instellingen > Algemeen > Starten met Windows** (standaard: uit, behalve als je in het welkomstvenster "Starten met aanbevolen instellingen" kiest, dan gaat het aan). Als het aan staat, wacht Portal AI Host na het aanmelden in het systeemvak, zodat apps er meteen gebruik van kunnen maken.
- Als de schakelaar zegt dat hij alleen in de geïnstalleerde app beschikbaar is, draai je een ontwikkelkopie.
- Als je het hebt uitgezet in Taakbeheer (opstart-apps), zet het daar weer aan.
- Als je organisatie dit instelt, is de schakelaar vergrendeld.

### Update

De Store werkt de app bij. Na een update wordt **Wat is nieuw** één keer geopend. Je kunt het opnieuw openen via het appmenu. Modellen blijven na updates op hun plaats.

### Verwijderen

Verwijder de app via **Windows-instellingen > Apps**. Modellen in de standaardmap staan in de gegevensmap van de app en worden met de app verwijderd. Een zelf gekozen modelmap blijft onaangetast. Zie hoofdstuk 15 voordat je verwijdert als je iets wilt bewaren.

---

## 3. Je weg vinden in de app

Aan de linkerkant staan vijf pagina's.

| Pagina | Waarvoor | Sneltoets |
|---|---|---|
| Start | Status, het actieve model, geheugengebruik, de app die nu AI gebruikt | Ctrl+1 |
| Modellen | Modellen downloaden, gebruiken en verwijderen | Ctrl+2 |
| Apparaten en apps | Portal-apps toestaan of verwijderen, pauzeren, prioriteit instellen | Ctrl+3 |
| Probeer het | Het model een korte vraag stellen | Ctrl+4 |
| Instellingen | Alles wat je kunt instellen | Ctrl+, |

Een zesde pagina, **Apparaten van deze pc**, toont de laptops en telefoons die met deze pc zijn gekoppeld (zie hoofdstuk 10). Het appmenu bevat Instellingen, Sneltoetsen, Wat is nieuw en Info.

**Start** laat zien of de AI **Klaar**, **Bezig met antwoorden**, **Model wordt voorbereid**, **Rust** (het model wordt wakker wanneer een app het vraagt) of nog **geen model** heeft. **NU** laat zien welke app een antwoord krijgt en ongeveer hoeveel woorden per seconde. Ook zie je hoeveel geheugen en videogeheugen het model gebruikt, en een knop **Nu laden** / **Nu ontladen**. **VANDAAG** telt de aanvragen per app. Een gezondheidskaart zegt "Alles in orde" of toont wat aandacht nodig heeft, elk met een oplossing met één klik, zoals Model laden, Engine opnieuw starten, Bestanden controleren of Prestatie-instellingen openen.

[screenshot: de pagina Start terwijl een app het model gebruikt]

Het pictogram in het systeemvak opent een klein venster: model, geheugen, laatste aanvraag en wachtrij, met de knoppen **App openen** en **Model ontladen**.

[screenshot: het pop-upvenster van het systeemvak]

Als het hostprogramma niet draait, biedt Start **Portal AI Host starten** aan. Als het meer dan één keer onverwacht is gesloten, geeft een balk **Veilige modus** aan dat het met voorzichtige instellingen is gestart. Je apps werken nog steeds; start de pc opnieuw als het vaker gebeurt.

---

## 4. Modellen

Een model is de AI die het werk doet. **Modellen** (Ctrl+2) toont wat je hebt en wat je kunt toevoegen.

### Aanbevolen model

De eerste start en de pagina Start raden "het beste model voor deze pc" aan, met een duidelijke reden, op basis van je geheugen, videogeheugen en vrije schijfruimte. Wanneer een beter model beschikbaar komt, toont een balk **Er is een beter model beschikbaar**. Je huidige model blijft tot het nieuwe klaar is, en jij bepaalt wanneer het oude wordt verwijderd.

### Een model downloaden

1. Open **Modellen**. Kies een model onder **Modellen die je kunt toevoegen**.
2. Druk op **Downloaden**. Een balk toont de voortgang en de resterende tijd.
3. **Pauzeren** en **Hervatten** kan altijd. **Stoppen** gooit het al gedownloade deel weg (na bevestiging).
4. De host controleert de gedownloade bestanden voordat ze worden gebruikt.
5. Als het klaar is, druk je op **Dit model gebruiken**.

Downloads kunnen na een herstart worden hervat en worden bestand voor bestand gecontroleerd. Een download heeft genoeg vrije schijfruimte nodig; als die er niet is, staat op de pagina hoeveel er ontbreekt en wordt **Map wijzigen in Instellingen** aangeboden. De pc blijft wakker terwijl een download loopt (Instellingen > Prestaties, standaard aan).

[screenshot: de pagina Modellen met een lopende download]

Een model met de melding "Dit model is misschien te groot voor deze pc" kan nog steeds worden gedownload, maar kan traag draaien of niet passen.

### Details, bestanden controleren, opnieuw downloaden, verwijderen

- **Details** toont waarvoor het model goed is, waar je op moet letten, het geheugen dat het nodig heeft, de licentie, wanneer het voor het laatst is gebruikt en hoeveel ruimte het inneemt.
- **Bestanden controleren** controleert een model opnieuw op schijf. Er wordt nooit iets verwijderd. Een beschadigd model krijgt de status **Beschadigd**.
- **Opnieuw downloaden** (voor een beschadigd model) verwijdert de beschadigde kopie en downloadt het opnieuw. Verder wordt niets verwijderd.
- **Verwijderen** haalt een model van deze pc na een bevestiging die aangeeft hoeveel ruimte vrijkomt. Als het model in gebruik is, ontlaadt de host het eerst en vraagt welk model daarna moet worden gebruikt. Alleen bestanden die de host zelf heeft gedownload, worden verwijderd.

### Je eigen model toevoegen

Met **Eigen model toevoegen...** kies je een `.gguf`-bestand. De host maakt een eigen kopie in de modelmap. Je origineel wordt nooit gewijzigd of verplaatst. De rij krijgt de markering **Door jou toegevoegd**, en de host kan het geheugen niet inschatten, dus probeer het uit. **Verwijderen** bij zo'n model verwijdert alleen de kopie van de host.

### Ruimte vrijmaken

**Ruimte vrijmaken** toont restanten die de app zelf heeft gemaakt: onvoltooide downloads die niet kunnen worden hervat, oude kopieën die zijn achtergebleven nadat je modellen zijn verplaatst, en modellen die een controle als beschadigd heeft gevonden. Je bevestigt voordat er iets wordt verwijderd. Modellen die je gebruikt en bestanden die je zelf hebt toegevoegd, komen nooit in de lijst. Onvoltooide downloads die je nog kunt hervatten, blijven een week bewaard.

### Modelmap en je modellen verplaatsen

**Instellingen > Modelmap** bepaalt waar nieuwe downloads naartoe gaan. Laat het leeg voor de standaardmap, of typ een volledig pad zoals `D:\Modellen` en druk op Enter. Modellen die al zijn gedownload, worden niet verplaatst of verwijderd. Ze blijven waar ze zijn en blijven in de lijst staan. Om ze te verplaatsen, accepteer je **Mijn modellen daarheen verplaatsen**. Ze worden gekopieerd, gecontroleerd en pas daarna worden de oude kopieën verwijderd. Het model dat in gebruik is, wordt tijdens dit proces ontladen, en **Verplaatsen annuleren** laat alles waar het was.

### Geheugenbudget en balken

De balken **Geheugenbudget** op de pagina Modellen tonen RAM en videogeheugen: hoeveel de geladen modellen gebruiken, hoeveel wordt gereserveerd terwijl een model laadt, en hoeveel er vrij is ten opzichte van je limieten (zie hoofdstuk 7). Een laadactie die je geheugenlimiet zou overschrijden, wordt geweigerd met een duidelijke reden voordat er iets wordt gestopt.

[screenshot: de balken Geheugenbudget met twee geladen modellen]

### Plaatsing: videokaart, geheugen of alleen processor

Waar een model draait, wordt bepaald door **Instellingen > Prestaties > Uitvoeren op** (Automatisch, Videokaart, Processor). Een model dat niet op de videokaart past, draait deels op de processor (trager). Zie ook de plaatsing per model in hoofdstuk 11, die **Alleen processor** bevat.

### Limieten en gratis of betaald

- Alle modellen in de lijst zijn gratis voor commercieel gebruik (licenties Apache 2.0 of MIT); elk model toont zijn eigen licentie.
- Modellen worden gedownload van de pagina's van hun uitgevers op Hugging Face. De host accepteert alleen een vertrouwde, ondertekende modellijst. Als die niet kan worden geladen, toont een balk **Modellijst niet beschikbaar**; modellen die al op deze pc staan, werken nog steeds.

### Problemen oplossen

- *Geen modellen om te downloaden zichtbaar:* controleer je internetverbinding en druk op **Opnieuw proberen** op Start.
- *Download stopt:* druk op **Hervatten**; als er weinig vrije ruimte is, maak ruimte vrij of wijzig de map.
- *Model "is misschien te groot":* kies een kleiner model, of verlaag de belasting in Instellingen > Prestaties.

---

## 5. Apps en machtigingen

Open **Apparaten en apps** (Ctrl+3).

- **Wacht op je antwoord:** een app die AI wilde gebruiken, verschijnt hier en als melding ("Voice Portal toestaan AI op deze pc te gebruiken?"). Kies **Toestaan**, **Niet toestaan** of **Later beslissen**. De melding toont of de app een geverifieerde Portal-app is, een geïnstalleerde app die geen Portal-app is, of niet geverifieerd (dan staat erbij vanaf waar hij draait; sta hem alleen toe als je hem zelf hebt gestart).
- **Apps die je AI-host kunnen gebruiken:** elke toegestane app toont de aanvragen van vandaag en wanneer hij voor het laatst is gebruikt.
  - **Pauzeren** weigert nieuwe aanvragen tot je hervat; de app behoudt zijn toegang.
  - **Prioriteit** (Hoog, Normaal, Laag) bepaalt wie als eerste wordt bediend wanneer meerdere apps tegelijk vragen. Interactieve aanvragen gaan voor batchwerk, en een aanvraag die lang wacht, schuift op zodat een app met lage prioriteit nooit wordt uitgehongerd.
  - **Toegang intrekken** stopt de app tot je hem weer toestaat. **Ongedaan maken** verschijnt even.

Apps moeten vragen voordat ze de host kunnen gebruiken. De host luistert alleen op deze pc, tenzij je delen met andere apparaten aanzet (hoofdstuk 9).

[screenshot: de pagina Apparaten en apps met één wachtende app en één toegestane app]

Voor ontwikkelaars: de host biedt een OpenAI-compatibele lokale API op `http://127.0.0.1:41870` (chat completions met streaming, embeddings, modellenlijst). Zie `api-v1.md`.

**Waar het wordt opgeslagen:** toegestane apps en hun tokens staan in de gegevensmap van de host, beveiligd voor jouw Windows-gebruiker. Toegang van een app intrekken verwijdert zijn token.

---

## 6. Probeer het

**Probeer het** (Ctrl+4) controleert of het model werkt. Typ een korte vraag en druk op **Vragen** (of Ctrl+Enter). **Stoppen** beëindigt het antwoord, **Wissen** maakt de pagina leeg. Er worden voorgestelde vragen aangeboden. Het toont hoe lang de eerste woorden duurden en de snelheid in tokens per seconde. Wat je hier typt, wordt niet opgeslagen.

[screenshot: Probeer het met een antwoord en de tijdregel]

---

## 7. Prestatie-instellingen

**Instellingen > Prestaties** voorkomt dat de AI je pc vertraagt. Wijzigingen werken meteen; een model dat niet meer past, wordt opnieuw geladen met de nieuwe limieten of ontladen (het blijft op schijf). **Standaard herstellen** zet de sectie terug.

| Instelling | Wat het doet | Standaard |
|---|---|---|
| Prestaties | Een voorinstelling: **Stil** (ongeveer de helft van de snelheid, de pc blijft reactief), **Gebalanceerd** (goede snelheid, je programma's gaan voor), **Snel** (snelste antwoorden, andere programma's kunnen trager worden), **Aangepast** | Gebalanceerd |
| Maximaal geheugen voor AI | De AI gebruikt nooit meer geheugen dan dit | 80% van het geheugen van de pc |
| Altijd vrij laten | Geheugen dat vrij blijft voor je andere programma's | Ingesteld door de voorinstelling |
| Uitvoeren op | **Automatisch** (videokaart wanneer dat helpt), **Videokaart** (het snelst, als het model past), **Processor** (trager, laat de videokaart vrij voor games) | Automatisch |
| Videogeheugen voor AI | Een limiet op het videogeheugen dat de AI mag gebruiken | Ingesteld door de voorinstelling |
| Processorkernen | Minder kernen houden je pc reactief | Ingesteld door de voorinstelling |
| Prioriteit | **Laag** (jouw werk eerst) of **Normaal** | Laag |
| Maximaal processorgebruik | Een plafond voor het processorgebruik | Geen limiet (100%) |
| Afremmen wanneer | De AI draait met de laagste prioriteit, gebruikt hoogstens de helft van je processor en keert daarna terug naar normaal. Kies een of meer van: een game of volledig-schermprogramma draait, de pc draait op batterij, een Portal-app is bezig met live werk zoals dicteren | Volledig-schermprogramma, batterij en bezige app aan |

Onder **Geheugen vrijgeven bij inactiviteit** in **Instellingen > Algemeen** kies je **Model geladen houden**, **Na 5 minuten** of **Na 10 minuten** (standaard 10 minuten, 5 op batterij). Het model laadt opnieuw de volgende keer dat een app erom vraagt. Het geladen houden geeft sneller antwoord, maar gebruikt de hele tijd geheugen.

De hints bij de schuifregelaars laten zien of het gekozen geheugen een model volledig op de videokaart past, gedeeltelijk op de processor of alleen op de processor, en bieden een kleiner model aan wanneer het huidige niet meer past.

[screenshot: Instellingen > Prestaties]

**Problemen oplossen:** trage antwoorden op een pc met videokaart betekenen meestal dat het model maar gedeeltelijk op de videokaart past. Kies een kleiner model, of verhoog het Videogeheugen voor AI. Als je pc hapert tijdens gebruik, kies Stil of verlaag het Maximale processorgebruik.

---

## 8. Meldingen

**Instellingen > Meldingen** regelt de korte Windows-berichten die verschijnen wanneer iets jouw aandacht nodig heeft en het venster gesloten is. Ze blijven stil terwijl een volledig-schermprogramma op de voorgrond staat, en herhalen zich nooit vaker dan eens in de paar minuten.

| Schakelaar | Betekenis |
|---|---|
| Een modeldownload is klaar of mislukt | Standaard aan |
| Een app vraagt om goedkeuring | Standaard aan |
| De AI-engine stopt of een controle mislukt | Standaard aan |
| Een apparaat koppelt of verbindt zich van buiten huis | Standaard aan (de meldingen uit hoofdstuk 10) |

Elke melding kan apart worden uitgezet. **Standaard herstellen** op dezelfde pagina zet ze terug.

---

## 9. Andere apparaten deze pc laten gebruiken

**Instellingen > Andere apparaten.** Standaard uit. Als het aan staat, kan je eigen laptop of een andere pc in je thuisnetwerk vragen om de AI van deze pc te gebruiken. Een apparaat moet eerst met een code worden gekoppeld, en elke app erop heeft nog steeds jouw toestemming nodig.

### Een apparaat koppelen

1. Zet **Andere apparaten de AI van deze pc laten gebruiken** aan. Windows vraagt misschien om een netwerkregel toe te staan.
2. Druk op **Een apparaat koppelen**. Er verschijnen een code en een QR-code, en **Koppelingslink kopiëren** kopieert een koppelingslink.
3. Open op het andere apparaat de bijbehorende Portal-app (of Portal AI Host in clientmodus, hoofdstuk 12), kies verbinden met een pc, en scan dan de code, typ hem in of plak de link.
4. Het controleert of het met deze pc praat voordat het iets verstuurt, en toont daarna de naam van de pc en een vingerafdruk om te bevestigen.

De code werkt één keer, 5 minuten lang, voor één apparaat. Verkeerde pogingen maken hem op; maak een nieuwe met **Een apparaat koppelen**. **Koppelen annuleren** beëindigt hem.

[screenshot: de koppelingscode en de QR-code]

### Gekoppelde apparaten

Elk gekoppeld apparaat wordt vermeld met de laatste gebruikstijd en een knop **Verwijderen**. Een apparaat verwijderen stopt de toegang meteen, en lopende aanvragen worden beëindigd. Het moet opnieuw koppelen om terug te keren. Er kunnen maximaal **16** apparaten worden gekoppeld.

### Overige instellingen op deze kaart

- **Deze pc ook buitenshuis bereiken** (de relay), zie hoofdstuk 13. Standaard: aan zodra andere apparaten zijn ingeschakeld.
- **Relaytoegangssleutel**, zie hoofdstuk 13. Standaard: uit.
- **Apparaten mogen modellen vervangen**, zie hoofdstuk 11. Standaard: uit.

**Wat er over het netwerk gaat:** aanvragen en antwoorden reizen versleuteld tussen je eigen apparaten. De verbinding op het thuisnetwerk gebruikt een certificaat dat het apparaat bij het koppelen heeft vastgezet. Er gaat niets naar een server van ons, behalve optioneel de blinde relay (hoofdstuk 13).

**Problemen oplossen:** *Een apparaat vindt deze pc niet:* controleer of beide op hetzelfde netwerk zitten, of deze pc wakker is en de host draait. *De code is "niet juist" of "verlopen":* maak een nieuwe. *"Niet op je thuisnetwerk":* koppel eerst één keer thuis.

---

## 10. Apparaten op deze pc

De pagina **Apparaten van deze pc** toont één kaart per gekoppelde laptop. Hier zie je wat elke laptop doet en kun je die beperken.

[screenshot: Apparaten van deze pc met twee apparaatkaarten]

### Wat een kaart toont

- Status (Online, Inactief, Offline), route (Thuisnetwerk of Relay (onderweg)) en laatst gezien.
- Wat er nu draait en wat er wacht.
- Aanvragen en tokens van vandaag, met een balk van 7 dagen, per app.
- Het model dat in gebruik is, wie het heeft geladen, en de snelheid in tokens per seconde en de tijd tot het eerste byte.

### Bediening

| Bediening | Wat het doet |
|---|---|
| Pauzeren / Hervatten | Pauzeert het hele apparaat, of één van zijn apps. De laptop krijgt een duidelijk antwoord "gepauzeerd" en gebruikt zijn eigen terugvaloptie, als je die hebt ingesteld. |
| Hernoemen | Wijzigt de weergegeven naam van het apparaat |
| Limieten | Prioriteit, dagelijkse limieten, rusturen en toegestane modellen (hieronder) |
| Modelbeheer (schakelaar) | Laat dit apparaat modellen laden en ontladen. Zie hoofdstuk 11 |
| Terugnemen | Ontlaadt het model dat dit apparaat heeft geladen. De eigen keuze van deze pc keert terug. Er wordt geen modelbestand aangeraakt. |
| Vergeten | Verwijdert het apparaat. De toegang stopt meteen en de gebruiksgeschiedenis wordt gewist. Modellen worden niet aangeraakt. |

**Limieten** (onder de knop **Limieten** op de kaart, daarna **Opslaan**):

- **Dezelfde prioriteit als de apps van deze pc.** Uit (standaard): de eigen apps van deze pc gaan eerst. Aan: het apparaat wacht in dezelfde rij.
- **Aanvragen per dag** en **Tokens per dag.** Standaard: geen limiet. Wanneer die op zijn, krijgt het apparaat te horen dat het na middernacht (tijd van deze pc) terug moet komen.
- **Rusturen**, met **Van** en **Tot** (tijd van deze pc). Het apparaat krijgt in die tijden geen antwoorden. Standaard: uit.
- **Modellen die dit apparaat mag gebruiken.** Standaard: alle modellen.

### Gamingmodus

Bovenaan de pagina. **Standaard: uit.** Als hij aan staat, krijgen aanvragen van andere apparaten het bericht dat deze pc bezet is terwijl je speelt, zodat games de videokaart houden. Deze pc telt als "aan het gamen" wanneer Windows een volledig-schermgame meldt, of wanneer een ander programma de 3D-engine van de videokaart ongeveer 10 seconden lang op 40% of meer houdt; games in een venster of een randloos venster tellen dus ook mee. Het stopt ongeveer 30 seconden nadat de belasting eindigt. Als de grafische tellers op je pc niet beschikbaar zijn, wordt alleen de volledig-schermcontrole gebruikt. De laptop valt dan terug op zijn eigen instellingen.

### Meldingen

Een Windows-melding verschijnt wanneer een nieuw apparaat koppelt, en wanneer een apparaat van buitenshuis via de relay verbinding maakt. Zet ze uit via **Instellingen > Meldingen > Een apparaat koppelt of verbindt zich van buiten huis**.

### Gegevens

Gebruiksgetallen zijn alleen tellingen (aanvragen en tokens per app per dag, 7 dagen bewaard). Er wordt geen tekst van prompts of antwoorden opgeslagen. **Vergeten** verwijdert alleen het gebruiksbestand van dat apparaat.

---

## 11. Modelbeheer en twee modellen tegelijk

Standaard gebruikt een gekoppelde laptop het model dat deze pc geladen heeft. Je kunt een apparaat meer rechten geven.

### Modelbeheer

**Instellingen > Andere apparaten**, op de rij van het apparaat: **Modelbeheer** (standaard: uit). Als dat aan staat, kan de laptop:
- geïnstalleerde modellen op deze pc laden en ontladen,
- kiezen waar een model draait (Automatisch, Videokaart, Systeemgeheugen of Alleen processor),
- kiezen welke modellen van deze pc elk van zijn eigen apps gebruikt.

Hij kan nooit modellen downloaden, verwijderen, controleren of importeren, instellingen wijzigen of aan je bestanden komen, met of zonder deze toestemming. Zet je het uit, dan wordt de volgende aanroep van het apparaat geweigerd. Het verwijderen van het apparaat haalt de toestemming weg, en opnieuw koppelen herstelt haar niet.

De pc toont wie wat heeft geladen: "Geladen door <apparaat>: <model> (videokaart)". **Terugnemen** (op de kaart en in Instellingen) ontlaadt het model dat een apparaat heeft geladen. De eigen keuze van deze pc keert terug en wordt bij de volgende aanvraag geladen.

### Twee modellen tegelijk

Met Modelbeheer mag een laptop een tweede model geladen houden op deze pc, bijvoorbeeld één op de videokaart en één in het systeemgeheugen.
- Je eigen model blijft in de eerste plek. Het model van een apparaat gaat naar de tweede plek, zodat een apparaat jouw model nooit verdringt om zijn eigen model te krijgen.
- Elke plek heeft zijn eigen wachtrij, zodat twee modellen tegelijk kunnen antwoorden. Een aanvraag die het tweede model noemt, gaat daarheen; al het andere gaat naar jouw model.
- Voor het laden telt de host wat de andere plek al gebruikt tegen je limieten. Een tweede model dat niet past, wordt geweigerd ("niet genoeg geheugen"). Er wordt niets stilletjes gestopt.
- Jouw keuzes gaan altijd voor. Kies je een ander model, ontlaad je er een, of heeft je eigen model het geheugen nodig, dan wordt het model van het apparaat gestopt en krijgt het apparaat bericht.
- Het tweede model van een apparaat wordt nooit eeuwig geladen gehouden; het ontlaadt zich, net als elk ander model, wanneer het inactief is.
- **Alleen processor** draait een model zonder grafische lagen, met ongeveer de helft van de processor; bijvoorbeeld een hulpmodel op de processor terwijl een ander de videokaart gebruikt.

[screenshot: de pagina Modellen met twee modellen geladen door verschillende eigenaren]

### Apparaten mogen modellen vervangen

**Instellingen > Andere apparaten > Apparaten mogen modellen vervangen** (standaard: uit). Uit: een apparaat kan geen model ontladen dat jouw apps gebruiken. Aan: het kan dat wel, en jouw apps laden hun model opnieuw bij hun volgende aanvraag. Bij twee apparaten behoudt het eerst geladen apparaat zijn model.

---

## 12. Je thuis-pc gebruiken vanaf een laptop (clientmodus)

Met de clientmodus gebruikt Portal AI Host op een laptop de AI van een andere pc van jou, in plaats van zelf een model te draaien. Apps op de laptop werken zoals altijd.

### Instellen

1. Zet op de thuis-pc **Andere apparaten de AI van deze pc laten gebruiken** aan en druk op **Een apparaat koppelen** (hoofdstuk 9). Druk op **Koppelingslink kopiëren** en stuur de link naar de laptop.
2. Open op de laptop **Instellingen > Thuis-pc** (of kies **Mijn thuis-pc gebruiken** in het welkomstvenster).
3. Kies bij **Waar de AI draait** de optie **Mijn thuis-pc gebruiken**. Er opent een venster **Mijn thuis-pc gebruiken**. Plak de link met **Link plakken**, druk op **Volgende**, controleer de naam van de pc en de vingerafdruk, en dan op **Koppelen**. Geef de laptop een naam.
4. Start toont nu de naam van de pc en een status: **Verbonden - thuisnetwerk**, **Verbonden - via relay**, **Verbinden...** of **Niet bereikbaar**.

[screenshot: het koppelvenster op de laptop]

### Instellingen (Instellingen > Thuis-pc)

| Instelling | Keuzes | Standaard |
|---|---|---|
| Waar de AI draait | Deze pc / Mijn thuis-pc gebruiken | Deze pc |
| Als de thuis-pc niet bereikbaar is | **Geen** (apps gebruiken hun ingebouwde AI) / **Model van deze laptop gebruiken** (laadt op verzoek) | Geen |
| Verbinding buitenshuis | **Automatisch** / **Handmatig** (dan geldt een schakelaar **Relay gebruiken** alleen voor deze sessie en wordt gereset wanneer de app sluit) | Automatisch |

- De eerste keer dat de thuis-pc niet bereikbaar is, wordt je één keer gevraagd wat er moet gebeuren; je kunt dit hier altijd wijzigen.
- **Pc koppelen** koppelt opnieuw; **Ontkoppelen** vergeet de pc (modellen en downloads blijven zoals ze zijn); **Pc wijzigen** (op Start) opent deze instellingen; **Hier AI draaien** (op Start) verlaat de clientmodus en bewaart de opgeslagen koppeling.
- Met Modelbeheer dat de thuis-pc heeft verleend, verschijnt **Modellen op <naam pc>** met knoppen voor laden, ontladen en plaatsing, plus **Pc-model per app**. Zonder die toestemming staat op de pagina: "Vraag de eigenaar van <naam pc> om Modelbeheer voor deze laptop aan te zetten".
- Een laptop in clientmodus accepteert geen verbindingen van andere apparaten en kan niet dienen als tussenstation naar je thuis-pc.

### Wat er gebeurt met de eigen modellen van deze laptop

Bij het overschakelen naar clientmodus wordt het geladen model van de laptop ontladen om geheugen vrij te maken. Modelbestanden, downloads en de modelmap blijven onaangetast. Bij het verlaten van de clientmodus wordt het model opnieuw geladen wanneer erom wordt gevraagd.

### Problemen oplossen

- **Niet bereikbaar:** controleer of de thuis-pc aan staat en op hetzelfde netwerk zit, of zet de relay aan.
- **"Deze laptop is van de pc verwijderd":** de thuis-pc heeft hem verwijderd. Kies **Opnieuw koppelen**.
- **"De identiteit van <pc> is veranderd":** het certificaat van de pc komt niet meer overeen met wat de laptop heeft opgeslagen. Als je dit niet verwachtte, koppel dan niet opnieuw. Ontkoppel anders eerst en koppel daarna opnieuw.
- **Koppelen van buitenaf mislukt:** koppel eerst thuis; de koppelingslink moet naar een adres op je thuisnetwerk verwijzen.
- **Gepauzeerd, rusturen, een game die draait of een dagelijkse limiet die op is:** de pc vertelt de laptop waarom; de laptop gebruikt zijn terugvaloptie.

**Waar het wordt opgeslagen:** de laptop bewaart zijn koppeling in de gegevensmap van de host, onder `client\`. De apparaatsleutel en tokens zijn beveiligd voor jouw Windows-gebruiker. **Ontkoppelen** verwijdert ze. Back-ups van die map kunnen niet worden teruggezet onder een andere Windows-gebruiker.

---

## 13. Weg van huis: de relay en de relaytoegangssleutel

Met de relay kan een gekoppeld apparaat je pc vanaf overal bereiken. **Instellingen > Andere apparaten > Deze pc ook buitenshuis bereiken.**

- Je pc maakt naar buiten verbinding met een kleine relaydienst. De relay stuurt alleen versleutelde bytes door die hij niet kan lezen, en bewaart geen inhoud. Elk apparaat moet nog steeds gekoppeld zijn en ondertekent elke aanvraag.
- De statusregel toont **Bereikbaar buitenshuis**, **Alleen thuisnetwerk**, **Verbinden met de relay...** of **Kan de relay niet bereiken**.
- Hij werkt via de gebruikelijke webpoort, dus de meeste kantoornetwerken staan het toe.
- **Zakelijke apparaten:** werkinhoud komt op je persoonlijke pc terecht. Controleer eerst de regels van je werkgever.

### Relaytoegangssleutel

**Instellingen > Andere apparaten > Relaytoegangssleutel** (standaard: uit). Als die aan staat, kunnen alleen apparaten die zijn gekoppeld met de sleutel in de koppelingscode deze pc via de relay bereiken. Zo kunnen vreemden de relayplekken van je pc niet opmaken.

- Zet hem **aan vóór het koppelen**. Eerder gekoppelde apparaten moeten opnieuw koppelen om de relay te gebruiken (het thuisnetwerk blijft werken).
- **Nieuwe sleutel** maakt na bevestiging een nieuwe sleutel. Elk gekoppeld apparaat moet dan opnieuw koppelen om de relay te gebruiken; thuis verandert er niets. Een apparaat verwijderen verandert de sleutel niet, dus gebruik **Nieuwe sleutel** na het verwijderen van een apparaat dat je niet vertrouwt.
- Als het sleutelbestand niet bruikbaar is, blijft de relay uit in plaats van open, en het thuisnetwerk blijft werken. Een beschadigd sleutelbestand wordt opzij gezet onder een naam met `.bad` en wordt nooit overschreven.
- Als de schakelaar niet aan blijft, moet de relaydienst mogelijk aan zijn kant worden bijgewerkt. Zet de schakelaar dan weer uit.

**Limieten:** de relay beperkt hoe snel en hoeveel een apparaat kan versturen, zodat één apparaat de dienst niet kan overspoelen. Normaal gebruik zit ruim onder deze limieten.

---

## 14. Privacy en crashrapporten

- **Geen analyses of gebruikstracering.** Alleen een crashrapport kan je pc verlaten, en alleen als je dat aan laat staan.
- **Je tekst blijft op je eigen apparaten.** Prompts en antwoorden worden door de host nooit opgeslagen of gelogd. Logs bevatten alleen tellingen, tijden en statussen.
- **Crashrapporten staan standaard aan, en je kunt ze uitzetten.** Als het venster of de achtergrondhost crasht, helpt een kort rapport ons het probleem op te lossen. Zet het op elk moment uit via **Instellingen > Privacy > Crashrapporten verzenden**; het werkt meteen. Het welkomstvenster biedt dezelfde keuze, standaard aangevinkt, voordat er iets wordt verzonden.
  - **Verzonden:** het type fout en waar die optrad (functie- en modulenamen), de app- en Windows-versies, of het een Store-build is, en of de fout uit het venster of uit de host kwam.
  - **Nooit verzonden:** prompts of antwoorden, wat je typt, bestandsnamen of mappaden, je computer- of gebruikersnaam, welke apps de host gebruiken, of je IP-adres. Foutteksten worden weggegooid.
  - **90 dagen bewaard**, daarna verwijderd.
  - **Wat wordt verzonden** (Instellingen > Privacy) opent de privacypagina.
- **Alles is versleuteld** wanneer het deze pc verlaat naar een ander van je apparaten.
- **Privacybeleid:** Info > **Privacybeleid**.
- Vragen, of een verzoek om een rapport te verwijderen: mindthaapp@gmail.com.

[screenshot: Instellingen > Privacy met de schakelaar voor crashrapporten]

---

## 15. Waar je gegevens staan, back-up en verwijderen

Alles staat in de eigen gegevensmap van de app. **Info > Gegevensmap openen** opent die.

| Wat | Waar (in de gegevensmap) | Back-up? |
|---|---|---|
| Gedownloade modellen | `models\`, of `Portal AI Host Models\` in de map die je in Instellingen hebt gekozen | Optioneel. Ze kunnen opnieuw worden gedownload. Ze zijn groot. |
| Instellingen | `settings.json` | Ja |
| Toegestane apps en hun tokens | De app-opslag van de host | Niet overdraagbaar (beveiligd voor jouw Windows-gebruiker) |
| Gekoppelde apparaten, limieten, gebruiksstatistieken | De apparatenopslag van de host en één klein statistiekenbestand per apparaat | Niet overdraagbaar |
| Laptopkoppeling (clientmodus) | `client\` | Niet overdraagbaar. Koppel opnieuw op een nieuwe pc. |
| Logs | `logs\` (roterend, beperkt tot 10 MB) | Nee |
| Vindbestand voor apps | `discovery\host.json` | Nee |

**Back-up:** sluit Portal AI Host en kopieer daarna de gegevensmap. Instellingen en modellen kunnen worden hersteld door ze terug te kopiëren. App-machtigingen, apparaatkoppelingen en tokens zijn beveiligd voor jouw Windows-gebruiker en jouw pc, dus op een nieuwe pc moet je apps opnieuw toestaan en apparaten opnieuw koppelen.

**Verwijderen:**
- *Eén model:* **Modellen > Verwijderen**. Alleen bestanden die de host heeft gedownload, worden verwijderd.
- *Restanten:* **Modellen > Ruimte vrijmaken**.
- *Een apparaat:* **Vergeten** (Apparaten van deze pc) of **Verwijderen** (Instellingen).
- *De toegang van een app:* **Toegang intrekken** (Apparaten en apps).
- *Alles:* verwijder de app. Een zelf gekozen modelmap blijft staan; verwijder zelf de map `Portal AI Host Models` als je die kwijt wilt.

De app verwijdert nooit iets wat hij niet zelf heeft gemaakt, overschrijft nooit je bestanden, en een model dat je zelf hebt toegevoegd wordt alleen verwijderd als de eigen kopie van de host (je oorspronkelijke bestand blijft onaangetast).

---

## 16. Sneltoetsen

| Toetsen | Actie |
|---|---|
| Ctrl+1 | Start |
| Ctrl+2 | Modellen |
| Ctrl+3 | Apparaten en apps |
| Ctrl+4 | Probeer het |
| Ctrl+, | Instellingen |
| F1 | Info |
| Ctrl+Enter | Vragen, op Probeer het |
| Esc | Wat is nieuw of een dialoogvenster sluiten |

De lijst staat ook onder het appmenu > **Sneltoetsen**.

Uiterlijk: **Instellingen > Uiterlijk** biedt Thema (standaard: je Windows-instelling), Venstermateriaal (Mica), Hoeken (Afgerond), Dichtheid (Standaard), Tekstgrootte (Normaal) en Animaties (Windows volgen). De hoogcontrastmodus van Windows gebruikt altijd effen systeemkleuren.

---

## 17. Problemen oplossen en veelgestelde vragen

**De host meldt dat hij niet draait.** Druk op **Portal AI Host starten** op Start. Als hij niet kan starten, start de pc opnieuw; lukt dat niet, installeer dan opnieuw vanuit de Store.

**"Een andere Windows-gebruiker op deze pc draait al Portal AI Host."** Vraag die gebruiker om hem af te sluiten, of meld je als die gebruiker aan.

**Antwoorden zijn traag.** Zie hoofdstuk 7. Een model dat te groot is voor je videogeheugen draait deels op de processor.

**Een app zegt dat er geen AI beschikbaar is.** Controleer of de app is toegestaan (Apparaten en apps), niet gepauzeerd is, en of er een model is geïnstalleerd dat niet te groot is voor de pc.

**Het eerste antwoord is traag, de volgende zijn snel.** Het model rustte. Het laadt bij de eerste aanvraag. Kies **Model geladen houden** in Instellingen > Algemeen om dit te voorkomen.

**Worden mijn gegevens ergens naartoe gestuurd?** Nee. Prompts en antwoorden blijven op je apparaten. Alleen crashrapporten kunnen weg (hoofdstuk 14), en je kunt ze uitzetten.

**Kan ik het zonder internet gebruiken?** Ja, zodra een model is gedownload.

**Kost het iets?** Nee.

**Welk model moet ik kiezen?** Het aanbevolen model. Kies een kleiner model als je pc traag aanvoelt, een groter model als je betere antwoorden wilt en het geheugen hebt.

**De download blijft hangen bij "Download controleren".** Wacht even. Grote bestanden hebben een minuut nodig om te worden gecontroleerd.

**Koppelen zegt "te veel apparaten".** De limiet is 16 apparaten. Verwijder er één.

**Waar is het pictogram in het systeemvak?** Klik op de pijl naast de klok om verborgen pictogrammen te tonen.

Als je nog steeds vastzit, gebruik dan **Info > Versie kopiëren**, schrijf naar mindthaapp@gmail.com en plak de versie in je bericht.

---

## 18. Later beschikbaar

Dit is gepland en nog niet in de app.

- **Modelcategorieën op de pagina Modellen:** gegroepeerd op taak (chat en schrijven, programmeren, redeneren, beeld, embeddings, spraak), met filters en zoeken.
- **Afbeeldings- en videogeneratie** op deze pc, inclusief een "regisseur" die shots aaneenrijgt tot een video. Videomodellen verschijnen als "Binnenkort" totdat de engine bestaat.
- **Kiezen welk model antwoordt**, en modellen die elkaar om hulp vragen, in AI Portal.
- **macOS-versie.**

---

## 19. Ondersteuning

- **Feedback en ideeën:** Info > **Feedback sturen of een idee voorstellen**.
- **Vragen over ondersteuning en privacy:** mindthaapp@gmail.com.
- **Versie:** Info toont hem, en **Versie kopiëren** zet hem op het klembord voor je bericht.
- **Licenties:** Portal AI Host bevat llama.cpp (MIT-licentie), dat de modellen op je pc draait. Modellen worden gedownload onder hun eigen licenties (Apache 2.0 of MIT). Zie Info.
