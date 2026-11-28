# Portal AI Host: användarguide

Portal AI Host kör en AI-modell på din dator och delar den med dina Portal-appar. Dina frågor och filer stannar på dina egna enheter. Den här guiden förklarar varje del av appen, vad inställningarna gör och vad du ska göra när något går fel.

Platser där en bild ska stå är markerade med `[screenshot: ...]`.

## Innehåll

1. Snabbstart
2. Systemkrav, installation, uppdatering, avinstallation
3. Hitta i appen
4. Modeller
5. Appar och behörigheter
6. Prova
7. Prestandainställningar
8. Aviseringar
9. Låt andra enheter använda den här datorn
10. Enheter på den här datorn
11. Modellkontroll och två modeller samtidigt
12. Använda din hemdator från en bärbar dator (klientläge)
13. Borta från hemmet: reläet och reläåtkomstnyckeln
14. Integritet och kraschrapporter
15. Var dina data lagras, säkerhetskopiering och borttagning
16. Kortkommandon
17. Felsökning och vanliga frågor
18. Kommer senare
19. Support

---

## 1. Snabbstart

1. Installera Portal AI Host från Microsoft Store och öppna appen.
2. Välkomstfönstret kontrollerar din dator (minne, grafikkort, ledigt diskutrymme) och rekommenderar en modell. Välj **Starta med rekommenderade inställningar**, eller **Ladda ner** för att välja modell själv. **Välj senare** hoppar över det här steget.
3. Leta efter ikonen bredvid klockan. Portal AI Host väntar tyst där. Klicka på den för en snabb statusöversikt.
4. Öppna **Prova** och ställ en kort fråga för att kontrollera att modellen fungerar.
5. Första gången en Portal-app vill använda AI visas en fråga om du vill tillåta eller neka.

[screenshot: välkomstfönstret med den rekommenderade modellen]

Om en bärbar dator i stället ska använda AI:n på din hemdator väljer du **Använd min hemdator** i välkomstfönstret. Se avsnitt 12.

**Gratis eller betalt:** Portal AI Host är gratis. Inget konto, ingen inloggning och inget köp krävs för någon funktion i den här guiden.

---

## 2. Systemkrav, installation, uppdatering, avinstallation

### Krav

| | Minimum | Rekommenderat |
|---|---|---|
| Windows | Windows 11 (21H2, build 22000) eller senare, x64 eller Arm64 | Samma |
| Minne | 8 GB | 16 GB eller mer (32 GB eller mer för de stora modellerna) |
| Grafik | Inget (modellen körs då på processorn, vilket är långsammare) | Ett Vulkan-kompatibelt grafikkort med 8 GB videominne eller mer (AMD, NVIDIA eller Intel) |
| Ledigt diskutrymme | 5 GB | 25 GB |

Modellerna är 2 till 20 GB var. Arm64-datorer kör modellen på processorn. En internetanslutning behövs bara för att ladda ner modeller och för reläet; när en modell väl finns på din dator fungerar den offline.

### Installation

Installera från Microsoft Store. Inget mer behövs: ingen Python, ingen kommandorad, ingen annan programvara. AI-motorn ingår i appen.

### Starta med Windows

**Inställningar > Allmänt > Starta med Windows** (standard: av, utom om du väljer "Starta med rekommenderade inställningar" i välkomstfönstret, vilket slår på det). När det är på väntar Portal AI Host i aktivitetsfältet efter att du loggat in, så att apparna kan använda det direkt.
- Om strömbrytaren säger att den bara är tillgänglig i den installerade appen kör du en utvecklingskopia.
- Om du stängde av det i Aktivitetshanteraren (appar som startar) slår du på det igen där.
- Om din organisation styr inställningen är strömbrytaren låst.

### Uppdatering

Store uppdaterar appen. Efter en uppdatering öppnas **Nyheter** en gång. Du kan öppna det igen från appmenyn. Modeller ligger kvar på sina platser mellan uppdateringarna.

### Avinstallation

Ta bort appen under **Windows-inställningar > Appar**. Modeller i standardmappen lagras i appens egen datamapp och tas bort tillsammans med den. En egen modellmapp som du valt påverkas inte. Se avsnitt 15 innan du avinstallerar om du vill behålla något.

---

## 3. Hitta i appen

Till vänster finns fem sidor.

| Sida | Till vad den används | Genväg |
|---|---|---|
| Start | Status, aktiv modell, minnesanvändning, appen som använder AI just nu | Ctrl+1 |
| Modeller | Ladda ner, använd och ta bort modeller | Ctrl+2 |
| Enheter och appar | Tillåt eller ta bort Portal-appar, pausa dem, ange prioritet | Ctrl+3 |
| Prova | Ställ en kort fråga till modellen | Ctrl+4 |
| Inställningar | Allt som går att konfigurera | Ctrl+, |

En sjätte sida, **Enheter på den här datorn**, visar de bärbara datorer och telefoner som är parkopplade med den här datorn (se avsnitt 10). Appmenyn innehåller Inställningar, Kortkommandon, Nyheter och Om.

**Start** visar om AI:n är **Redo**, **Svarar**, **Förbereder modellen**, **Vilar** (modellen vaknar när en app ber om det) eller **inte har någon modell ännu**. **JUST NU** visar vilken app som får ett svar och ungefär hur många ord per sekund. Där syns också hur mycket minne och videominne modellen använder, samt en knapp **Läs in nu** / **Läs ur nu**. **IDAG** räknar förfrågningarna per app. Ett hälsokort visar "Allt bra" eller listar det som behöver uppmärksamhet, var och en med en åtgärd med ett klick, till exempel Läs in modellen, Starta om motorn, Kontrollera filer eller Öppna prestandainställningar.

[screenshot: sidan Start medan en app använder modellen]

Ikonen i aktivitetsfältet öppnar ett litet fönster: modell, minne, senaste förfrågan och kö, med knapparna **Öppna app** och **Läs ur modell**.

[screenshot: fönstret från aktivitetsfältet]

Om värdprogrammet inte körs erbjuder Start **Starta Portal AI Host**. Om det har stängts oväntat mer än en gång visas en **Felsäkert läge**-rad som säger att det startade med försiktiga inställningar. Dina appar fungerar fortfarande; starta om datorn om det fortsätter.

---

## 4. Modeller

En modell är den AI som gör arbetet. **Modeller** (Ctrl+2) visar vad du har och vad du kan lägga till.

### Rekommenderad modell

Den första starten och sidan Start rekommenderar "den bästa modellen för den här datorn", med en tydlig motivering, utifrån ditt minne, ditt videominne och ditt lediga diskutrymme. När en bättre modell blir tillgänglig visar en rad **En bättre modell finns tillgänglig**. Din nuvarande modell finns kvar tills den nya är klar, och du bestämmer när den gamla tas bort.

### Ladda ner en modell

1. Öppna **Modeller**. Välj en modell under **Modeller du kan lägga till**.
2. Tryck på **Ladda ner**. En rad visar förloppet och återstående tid.
3. **Pausa** och **Återuppta** fungerar när som helst. **Stoppa** kasserar den del som redan laddats ner (efter bekräftelse).
4. Värdprogrammet kontrollerar de nedladdade filerna innan de används.
5. När det är klart trycker du på **Använd den här modellen**.

Nedladdningar kan återupptas efter en omstart och kontrolleras fil för fil. En nedladdning behöver tillräckligt med ledigt diskutrymme; om det inte finns visar sidan hur mycket som saknas och erbjuder **Ändra mapp i Inställningar**. Datorn hålls vaken medan en nedladdning pågår (Inställningar > Prestanda, på som standard).

[screenshot: sidan Modeller med en pågående nedladdning]

En modell som är märkt "Den här modellen kan vara för stor för den här datorn" kan fortfarande laddas ner, men kan vara långsam eller inte få plats.

### Detaljer, kontrollera filer, ladda ner igen, ta bort

- **Detaljer** visar vad modellen är bra för, vad du bör tänka på, minnet den behöver, dess licens, när den senast användes och hur mycket utrymme den tar.
- **Kontrollera filer** kontrollerar en modell på disken igen. Det raderar aldrig något. En skadad modell markeras som **Skadad**.
- **Ladda ner igen** (för en skadad modell) tar bort den skadade kopian och laddar ner den på nytt. Inget annat raderas.
- **Ta bort** tar bort en modell från den här datorn efter en bekräftelse som anger hur mycket utrymme som frigörs. Om modellen används läser värdprogrammet först ur den och frågar vad som ska användas härnäst. Endast filer som värdprogrammet själv laddat ner raderas.

### Lägg till din egen modell

**Lägg till din egen modell...** låter dig välja en `.gguf`-fil. Värdprogrammet gör en egen kopia i modellmappen. Originalet ändras eller flyttas aldrig. Raden märks **Tillagd av dig**, och värdprogrammet kan inte uppskatta hur mycket minne den behöver, så prova och se. **Ta bort** på en sådan modell raderar bara värdprogrammets kopia.

### Frigör utrymme

**Frigör utrymme** listar rester som appen själv skapat: ofullständiga nedladdningar som inte kan återupptas, gamla kopior som blivit kvar efter att du flyttat dina modeller och modeller som en kontroll hittat som skadade. Du bekräftar innan något tas bort. Modeller du använder och filer du lagt till själv visas aldrig i listan. Ofullständiga nedladdningar som du fortfarande kan återuppta sparas i en vecka.

### Modellmapp och flytt av dina modeller

**Inställningar > Modellmapp** anger var nya nedladdningar hamnar. Lämna tomt för standardmappen, eller skriv en fullständig sökväg som `D:\Modeller` och tryck på Enter. Modeller som redan laddats ner flyttas eller raderas inte. De ligger kvar där de är och visas fortfarande i listan. För att flytta dem, acceptera **Flytta mina modeller hit**. De kopieras, kontrolleras och först därefter tas de gamla kopiorna bort. Modellen som används läses ur under tiden, och **Avbryt flytt** lämnar allt där det var.

### Minnesbudget och staplar

Staplarna **Minnesbudget** på sidan Modeller visar RAM och videominne: hur mycket de inlästa modellerna använder, hur mycket som reserveras medan en modell läses in och hur mycket som är ledigt i förhållande till dina gränser (se avsnitt 7). En inläsning som skulle överskrida din minnesgräns avvisas med en tydlig förklaring innan något stoppas.

[screenshot: staplarna för minnesbudget med två inlästa modeller]

### Placering: grafikkort, minne eller bara processor

Var en modell körs avgörs av **Inställningar > Prestanda > Kör på** (Automatiskt, Grafikkort, Processor). En modell som inte får plats på grafikkortet körs delvis på processorn (långsammare). Se även placeringen per modell i avsnitt 11, som inkluderar **Endast processor**.

### Gränser och gratis eller betalt

- Alla modeller i listan är gratis för kommersiell användning (licenserna Apache 2.0 eller MIT); varje modell visar sin egen licens.
- Modellerna laddas ner från utgivarnas sidor på Hugging Face. Värdprogrammet accepterar endast en betrodd, signerad modellista. Om den inte kan läsas in visar en rad **Modellistan är inte tillgänglig**; modeller som redan finns på den här datorn fungerar fortfarande.

### Felsökning

- *Inga modeller visas för nedladdning:* kontrollera din internetanslutning och tryck på **Försök igen** på Start.
- *Nedladdningen stannar:* tryck på **Återuppta**; om det är ont om ledigt utrymme, frigör utrymme eller ändra mappen.
- *Modellen "kan vara för stor":* välj en mindre modell, eller sänk belastningen i Inställningar > Prestanda.

---

## 5. Appar och behörigheter

Öppna **Enheter och appar** (Ctrl+3).

- **Väntar på ditt svar:** en app som bad om att få använda AI visas här och som en fråga ("Tillåta Voice Portal att använda AI på den här datorn?"). Välj **Tillåt**, **Tillåt inte** eller **Besluta senare**. Frågan visar om appen är en verifierad Portal-app, en installerad app som inte är en Portal-app, eller inte verifierad (då anges varifrån den körs; tillåt den bara om du startat den själv).
- **Appar som kan använda din AI-värd:** varje tillåten app visar dagens förfrågningar och när den senast användes.
  - **Pausa** avvisar nya förfrågningar tills du återupptar; appen behåller sin åtkomst.
  - **Prioritet** (Hög, Normal, Låg) avgör vem som går först när flera appar frågar samtidigt. Interaktiva förfrågningar går före batchjobb, och en förfrågan som väntar länge flyttas upp så att en app med låg prioritet aldrig blir utan.
  - **Ta bort åtkomst** stoppar appen tills du tillåter den igen. **Ångra** visas en kort stund.

Appar måste fråga innan de kan använda värdtjänsten. Värdtjänsten lyssnar bara på den här datorn, om du inte slår på delning med andra enheter (avsnitt 9).

[screenshot: sidan Enheter och appar med en väntande app och en tillåten app]

För utvecklare: värdtjänsten erbjuder ett lokalt API som är kompatibelt med OpenAI på `http://127.0.0.1:41870` (chat completions med strömning, inbäddningar, modellista). Se `api-v1.md`.

**Var det lagras:** tillåtna appar och deras token sparas i värdtjänstens datamapp, skyddade för ditt Windows-användarkonto. Om du tar bort en apps åtkomst tas dess token bort.

---

## 6. Prova

**Prova** (Ctrl+4) kontrollerar att modellen fungerar. Skriv en kort fråga och tryck på **Fråga** (eller Ctrl+Enter). **Stoppa** avslutar svaret, **Rensa** tömmer sidan. Förslag på frågor erbjuds. Den visar hur lång tid de första orden tog och hastigheten i token per sekund. Det du skriver här sparas inte.

[screenshot: Prova med ett svar och tidsraden]

---

## 7. Prestandainställningar

**Inställningar > Prestanda** hindrar AI:n från att göra datorn långsam. Ändringarna gäller direkt; en modell som inte längre får plats läses in igen med de nya gränserna eller läses ur (den finns kvar på disken). **Återställ standardvärden** återställer avsnittet.

| Inställning | Vad den gör | Standard |
|---|---|---|
| Prestanda | En förinställning: **Tyst** (ungefär halva hastigheten, datorn svarar fortfarande bra), **Balanserad** (bra hastighet, dina program går först), **Snabb** (snabbaste svar, andra program kan bli långsammare), **Anpassad** | Balanserad |
| Maxminne för AI | AI:n använder aldrig mer minne än så här | 80 % av datorns minne |
| Håll alltid ledigt | Minne som hålls ledigt för dina andra program | Angett av förinställningen |
| Kör på | **Automatiskt** (grafikkort när det hjälper), **Grafikkort** (snabbast, om modellen får plats), **Processor** (långsammare, lämnar grafikkortet ledigt för spel) | Automatiskt |
| Videominne för AI | En gräns för hur mycket videominne AI:n får använda | Angett av förinställningen |
| Processorkärnor | Färre kärnor håller datorn responsiv | Angett av förinställningen |
| Prioritet | **Låg** (ditt arbete först) eller **Normal** | Låg |
| Högsta processoranvändning | Ett tak för processoranvändningen | Ingen gräns (100 %) |
| Dra ner när | AI:n körs med lägsta prioritet, använder högst hälften av din processor och återgår till normalt när den slutar. Välj någon av: ett spel eller program i helskärm körs, datorn är på batteri, en Portal-app är upptagen med liveuppgift som diktering | Helskärmsprogram, batteri och upptagen app på |

Under **Frigör minne vid inaktivitet** i **Inställningar > Allmänt** väljer du **Håll modellen inläst**, **Efter 5 minuter** eller **Efter 10 minuter** (standard 10 minuter, 5 på batteri). Modellen läses in igen nästa gång en app frågar. Att hålla den inläst ger snabbare svar, men använder minne hela tiden.

Reglagens tips visar om det valda minnet får plats med en modell helt på grafikkortet, delvis på processorn eller bara på processorn, och erbjuder en mindre modell när den nuvarande inte längre får plats.

[screenshot: Inställningar > Prestanda]

**Felsökning:** långsamma svar på en dator med grafikkort betyder oftast att modellen bara delvis får plats på grafikkortet. Välj en mindre modell eller höj Videominne för AI. Om datorn hackar under användning, välj Tyst eller sänk Högsta processoranvändning.

---

## 8. Aviseringar

**Inställningar > Aviseringar** styr de korta Windows-meddelanden som visas när något behöver din uppmärksamhet och fönstret är stängt. De håller sig tysta när ett helskärmsprogram är i förgrunden, och upprepas aldrig oftare än var några minuter.

| Reglage | Betydelse |
|---|---|
| En modellnedladdning slutförs eller misslyckas | På som standard |
| En app begär godkännande | På som standard |
| AI-motorn stannar eller en kontroll misslyckas | På som standard |
| En enhet parkopplas eller ansluter utifrån | På som standard (aviseringarna i avsnitt 10) |

Var och en kan stängas av separat. **Återställ standardvärden** på samma sida återställer dem.

---

## 9. Låt andra enheter använda den här datorn

**Inställningar > Andra enheter.** Av som standard. När det är på kan din egen bärbara dator eller en annan dator i ditt hemnätverk be om att få använda AI:n på den här datorn. En enhet måste först parkopplas med en kod, och varje app på den behöver fortfarande ditt godkännande.

### Parkoppla en enhet

1. Slå på **Låt andra enheter använda den här datorns AI**. Windows kan be dig tillåta en nätverksregel.
2. Tryck på **Parkoppla en enhet**. En kod och en QR-kod visas, och **Kopiera länk** kopierar en parkopplingslänk.
3. På den andra enheten öppnar du dess Portal-app (eller Portal AI Host i klientläge, avsnitt 12), väljer att ansluta till en dator och skannar sedan koden, skriver in den eller klistrar in länken.
4. Den kontrollerar att den pratar med den här datorn innan den skickar något, och visar sedan datorns namn och ett fingeravtryck att bekräfta.

Koden fungerar en gång, i 5 minuter, för en enhet. Felaktiga försök förbrukar den; skapa en ny med **Parkoppla en enhet**. **Avbryt parkoppling** avslutar den.

[screenshot: parkopplingskoden och QR-koden]

### Parkopplade enheter

Varje parkopplad enhet visas med tiden för senaste användning och en knapp **Ta bort**. Att ta bort en enhet stoppar dess åtkomst direkt, och pågående förfrågningar avslutas. Den måste parkopplas igen för att komma tillbaka. Högst **16** enheter kan vara parkopplade.

### Andra inställningar på det här kortet

- **Nå även den här datorn utanför hemmet** (reläet), se avsnitt 13. Standard: på när andra enheter är aktiverade.
- **Reläåtkomstnyckel**, se avsnitt 13. Standard: av.
- **Enheter får ersätta modeller**, se avsnitt 11. Standard: av.

**Vad som går över nätverket:** förfrågningar och svar reser krypterade mellan dina egna enheter. Anslutningen på hemnätverket använder ett certifikat som enheten fäst vid parkopplingen. Ingenting går till en server hos oss, utom, om du väljer det, det blinda reläet (avsnitt 13).

**Felsökning:** *En enhet hittar inte den här datorn:* kontrollera att båda är på samma nätverk, att datorn är vaken och att värdtjänsten körs. *Koden är "inte korrekt" eller "har gått ut":* skapa en ny. *"Inte på ditt hemnätverk":* parkoppla en gång hemma först.

---

## 10. Enheter på den här datorn

Sidan **Enheter på den här datorn** visar ett kort per parkopplad bärbar dator. Det är här du ser vad varje bärbar dator gör och kan begränsa den.

[screenshot: Enheter på den här datorn med två enhetskort]

### Vad ett kort visar

- Status (Online, Inaktiv, Offline), väg (Hemnätverk eller Relä (borta)) och senast sedd.
- Vad som körs nu och vad som väntar.
- Dagens förfrågningar och token, med en 7-dagarsrad, per app.
- Modellen i användning, vem som läste in den, samt hastighet i token per sekund och tid till första byte.

### Kontroller

| Kontroll | Vad den gör |
|---|---|
| Pausa / Återuppta | Pausar hela enheten, eller en av dess appar. Den bärbara datorn får ett tydligt svar "pausad" och använder sin egen reservlösning, om du har angett en. |
| Byt namn | Ändrar det visade namnet på enheten |
| Gränser | Prioritet, dagliga gränser, tysta tider och tillåtna modeller (nedan) |
| Modellkontroll (reglage) | Låter den här enheten läsa in och läsa ur modeller. Se avsnitt 11 |
| Ta tillbaka | Läser ur modellen som enheten läste in. Datorns eget val återgår. Inga modellfiler påverkas. |
| Glöm | Tar bort enheten. Dess åtkomst upphör direkt och dess användningshistorik raderas. Modeller påverkas inte. |

**Gränser** (under kortets knapp **Gränser**, sedan **Spara**):

- **Samma prioritet som den här datorns appar.** Av (standard): den här datorns egna appar går först. På: enheten väntar i samma kö.
- **Förfrågningar per dag** och **Token per dag.** Standard: ingen gräns. När de är slut får enheten besked att komma tillbaka efter midnatt (den här datorns tid).
- **Tysta tider**, med **Från** och **Till** (den här datorns tid). Enheten får inga svar under dessa tider. Standard: av.
- **Modeller som den här enheten får använda.** Standard: alla modeller.

### Spelläge

Längst upp på sidan. **Standard: av.** När det är på får förfrågningar från andra enheter beskedet att den här datorn är upptagen medan du spelar, så att spelen behåller grafikkortet. Den här datorn räknas som "spelar" när Windows rapporterar ett helskärmsspel, eller när ett annat program håller grafikkortets 3D-motor på 40 % eller mer i cirka 10 sekunder; spel i fönster eller i fönster utan kanter räknas alltså också. Det slutar räknas cirka 30 sekunder efter att belastningen upphör. Om grafikräknarna inte är tillgängliga på din dator används bara helskärmskontrollen. Den bärbara datorn faller då tillbaka på sina egna inställningar.

### Aviseringar

En Windows-avisering visas när en ny enhet parkopplas och när en enhet ansluter via reläet utifrån. Stäng av dem under **Inställningar > Aviseringar > En enhet parkopplas eller ansluter utifrån**.

### Data

Användningssiffrorna är enbart räknare (förfrågningar och token per app och dag, 7 dagar sparas). Ingen text från frågor eller svar lagras. **Glöm** tar bara bort användningsfilen för den enheten och ingenting annat.

---

## 11. Modellkontroll och två modeller samtidigt

Som standard använder en parkopplad bärbar dator den modell som den här datorn har läst in. Du kan ge en enhet mer.

### Modellkontroll

**Inställningar > Andra enheter**, på enhetens rad: **Modellkontroll** (standard: av). Med den på kan den bärbara datorn:
- läsa in och läsa ur installerade modeller på den här datorn,
- välja var en modell körs (Automatiskt, Grafikkort, Systemminne eller Endast processor),
- välja vilka av den här datorns modeller var och en av dess egna appar använder.

Den kan aldrig ladda ner, ta bort, kontrollera eller importera modeller, ändra inställningar eller röra dina filer, oavsett om den har den här behörigheten eller inte. Stänger du av den avvisas enhetens nästa anrop. Att ta bort enheten tar bort behörigheten, och att parkoppla den igen återställer den inte.

Datorn visar vem som läst in vad: "Inläst av <enhet>: <modell> (grafikkort)". **Ta tillbaka** (på kortet och i Inställningar) läser ur modellen som en enhet läst in. Datorns eget val återgår och läses in vid nästa förfrågan.

### Två modeller samtidigt

Med Modellkontroll kan en bärbar dator hålla en andra modell inläst på den här datorn, till exempel en på grafikkortet och en i systemminnet.
- Din egen modell stannar i den första platsen. En enhets modell hamnar på den andra, så att en enhet aldrig tränger undan din modell bara för att få sin egen.
- Varje plats har sin egen kö, så att två modeller kan svara samtidigt. En förfrågan som nämner den andra modellen går till den; allt annat går till din egen.
- Före inläsning räknar värdtjänsten vad den andra platsen redan använder mot dina gränser. En andra modell som inte får plats avvisas ("inte tillräckligt med minne"). Ingenting stoppas i tysthet.
- Dina egna val går alltid först. Väljer du en annan modell, läser ur en, eller behöver din egen modell minnet, stoppas enhetens modell och enheten får besked.
- En enhets andra modell hålls aldrig inläst för evigt; den läses ur som alla andra när den är inaktiv.
- **Endast processor** kör en modell utan grafiklager, med ungefär hälften av processorn; till exempel en hjälpmodell på processorn medan en annan använder grafikkortet.

[screenshot: sidan Modeller med två modeller inlästa av olika ägare]

### Enheter får ersätta modeller

**Inställningar > Andra enheter > Enheter får ersätta modeller** (standard: av). Av: en enhet kan inte läsa ur en modell som dina egna appar använder. På: den kan det, och dina appar läser in sin modell igen vid nästa förfrågan. Med två enheter behåller den som läste in först sin modell.

---

## 12. Använda din hemdator från en bärbar dator (klientläge)

Klientläge gör att Portal AI Host på en bärbar dator använder AI:n på en annan av dina datorer i stället för att köra en modell själv. Apparna på den bärbara datorn fungerar som vanligt.

### Konfigurera

1. Slå på **Låt andra enheter använda den här datorns AI** på hemdatorn och tryck på **Parkoppla en enhet** (avsnitt 9). Tryck på **Kopiera länk** och skicka länken till den bärbara datorn.
2. Öppna **Inställningar > Hemdator** på den bärbara datorn (eller välj **Använd min hemdator** i välkomstfönstret).
3. Under **Var AI:n körs** väljer du **Använd min hemdator**. Ett fönster **Använd min hemdator** öppnas. Klistra in länken med **Klistra in länk**, tryck på **Nästa**, kontrollera datorns namn och fingeravtryck och sedan **Parkoppla**. Ge den bärbara datorn ett namn.
4. Start visar nu datorns namn och en status: **Ansluten - hemnätverk**, **Ansluten - via relä**, **Ansluter...** eller **Kan inte nås**.

[screenshot: parkopplingsfönstret på den bärbara datorn]

### Inställningar (Inställningar > Hemdator)

| Inställning | Val | Standard |
|---|---|---|
| Var AI:n körs | Den här datorn / Använd min hemdator | Den här datorn |
| Om hemdatorn inte kan nås | **Ingen** (apparna använder sin inbyggda AI) / **Använd den här bärbara datorns modell** (läses in vid behov) | Ingen |
| Anslutning utanför hemmet | **Automatisk** / **Manuell** (då gäller en **Använd relä**-brytare bara för den här sessionen och återställs när appen stängs) | Automatisk |

- Första gången hemdatorn inte kan nås får du en fråga om vad som ska hända; du kan ändra det här när som helst.
- **Parkoppla dator** parkopplar igen; **Koppla från** glömmer datorn (modeller och nedladdningar ligger kvar som de är); **Byt dator** (på Start) öppnar dessa inställningar; **Kör AI här** (på Start) lämnar klientläget och behåller den sparade parkopplingen.
- Med Modellkontroll som hemdatorn har gett visas **Modeller på <datornamn>** med knappar för inläsning, urläsning och placering, plus **Datorns modell för varje app**. Utan den behörigheten står det på sidan: "Be ägaren av <datornamn> att slå på Modellkontroll för den här bärbara datorn".
- En bärbar dator i klientläge tar inte emot anslutningar från andra enheter och kan inte vara ett mellanled till din hemdator.

### Vad händer med den bärbara datorns egna modeller

När man går in i klientläget läses den modell som är inläst på den bärbara datorn ur för att frigöra minne. Modellfiler, nedladdningar och modellmappen påverkas inte. När man lämnar klientläget läses modellen in igen när den begärs.

### Felsökning

- **Kan inte nås:** kontrollera att hemdatorn är på och på samma nätverk, eller slå på reläet.
- **"Den här bärbara datorn togs bort från datorn":** hemdatorn tog bort den. Välj **Parkoppla igen**.
- **"Identiteten för <dator> har ändrats":** datorns certifikat stämmer inte längre med det den bärbara datorn sparade. Om du inte väntade dig detta, parkoppla inte igen. Annars, koppla från och parkoppla sedan igen.
- **Parkoppling utanför hemmet misslyckas:** parkoppla först hemma; parkopplingslänken måste peka på en adress i ditt hemnätverk.
- **Pausad, tysta tider, ett spel som körs eller en daglig gräns som är slut:** datorn talar om för den bärbara datorn varför; den bärbara datorn använder sin reservlösning.

**Var det lagras:** den bärbara datorn sparar sin parkoppling i värdtjänstens datamapp under `client\`. Enhetsnyckeln och token skyddas för ditt Windows-användarkonto. **Koppla från** tar bort dem. Säkerhetskopior av den mappen kan inte återställas under ett annat Windows-användarkonto.

---

## 13. Borta från hemmet: reläet och reläåtkomstnyckeln

Reläet låter en parkopplad enhet nå din dator var som helst. **Inställningar > Andra enheter > Nå även den här datorn utanför hemmet.**

- Din dator ansluter utåt till en liten relätjänst. Reläet vidarebefordrar bara krypterade byte som det inte kan läsa, och det sparar inget innehåll. Varje enhet måste fortfarande vara parkopplad och signerar varje förfrågan.
- Statusraden visar **Nåbar utanför hemmet**, **Endast hemnätverk**, **Ansluter till reläet...** eller **Kan inte nå reläet**.
- Det fungerar över den vanliga webbporten, så de flesta kontorsnät tillåter det.
- **Företagsenheter:** arbetsinnehåll hamnar på din personliga dator. Kontrollera först din arbetsgivares regler.

### Reläåtkomstnyckel

**Inställningar > Andra enheter > Reläåtkomstnyckel** (standard: av). När den är på kan bara enheter som parkopplats med nyckeln i parkopplingskoden nå den här datorn via reläet, vilket hindrar främlingar från att ta upp din dators reläplatser.

- Slå på den **innan du parkopplar**. Enheter som parkopplats tidigare måste parkopplas igen för att använda reläet (hemnätverket fungerar fortfarande).
- **Ny nyckel** skapar en ny nyckel efter bekräftelse. Varje parkopplad enhet måste då parkopplas igen för att använda reläet; hemma ändras ingenting. Att ta bort en enhet ändrar inte nyckeln, så använd **Ny nyckel** efter att du tagit bort en enhet som du inte litar på.
- Om nyckelfilen inte kan användas förblir reläet avstängt i stället för öppet, och hemnätverket fungerar fortfarande. En skadad nyckelfil flyttas åt sidan under ett namn med `.bad` och skrivs aldrig över.
- Om reläkontakten inte stannar på kan relätjänsten behöva uppdateras på dess sida. Stäng av reläkontakten igen.

**Gränser:** reläet begränsar hur snabbt och hur mycket en enhet kan skicka, så att en enda enhet inte kan överösa tjänsten. Normal användning ligger långt under dessa gränser.

---

## 14. Integritet och kraschrapporter

- **Inga analyser eller användningsspårning.** Endast en kraschrapport kan lämna din dator, och bara om du låter det vara på.
- **Din text stannar på dina enheter.** Frågor och svar sparas eller loggas aldrig av värdtjänsten. Loggarna innehåller bara räknare, tider och tillstånd.
- **Kraschrapporter är på som standard, och du kan stänga av dem.** Om fönstret eller värdtjänsten i bakgrunden kraschar hjälper en kort rapport oss att rätta felet. Stäng av det när som helst under **Inställningar > Integritet > Skicka kraschrapporter**; det gäller direkt. Välkomstfönstret har samma val, ikryssat som standard, innan något skickas.
  - **Skickas:** feltypen och var det inträffade (funktions- och modulnamn), app- och Windows-versionerna, om det är en Store-version, och om felet kom från fönstret eller värdtjänsten.
  - **Skickas aldrig:** frågor eller svar, det du skriver, filnamn eller mappsökvägar, ditt dator- eller användarnamn, vilka appar som använder värdtjänsten, eller din IP-adress. Feltext kasseras.
  - **Sparas i 90 dagar**, sedan raderas de.
  - **Vad som skickas** (Inställningar > Integritet) öppnar integritetssidan.
- **Allt är krypterat** när det lämnar den här datorn till en annan av dina enheter.
- **Integritetspolicy:** Om > **Integritetspolicy**.
- Frågor, eller en begäran om att radera en rapport: mindthaapp@gmail.com.

[screenshot: Inställningar > Integritet med reläkontakten för kraschrapporter]

---

## 15. Var dina data lagras, säkerhetskopiering och borttagning

Allt finns i appens egen datamapp. **Om > Öppna datamapp** öppnar den.

| Vad | Var (i datamappen) | Säkerhetskopiera? |
|---|---|---|
| Nedladdade modeller | `models\`, eller `Portal AI Host Models\` i mappen du valde i Inställningar | Valfritt. De kan laddas ner igen. De är stora. |
| Inställningar | `settings.json` | Ja |
| Tillåtna appar och deras token | Värdtjänstens appregister | Inte flyttbart (skyddat för ditt Windows-användarkonto) |
| Parkopplade enheter, gränser, användningsstatistik | Värdtjänstens enhetsregister och en liten statistikfil per enhet | Inte flyttbart |
| Parkoppling av bärbar dator (klientläge) | `client\` | Inte flyttbart. Parkoppla igen på en ny dator. |
| Loggar | `logs\` (rullande, begränsad till 10 MB) | Nej |
| Identifieringsfil för appar | `discovery\host.json` | Nej |

**Säkerhetskopiera:** stäng Portal AI Host och kopiera datamappen. Inställningar och modeller kan återställas genom att kopiera tillbaka dem. Appbehörigheter, enhetsparkopplingar och token är skyddade för ditt Windows-användarkonto och din dator, så på en ny dator tillåter du appar och parkopplar enheter igen.

**Ta bort:**
- *En modell:* **Modeller > Ta bort**. Endast filer som värdtjänsten laddat ner raderas.
- *Rester:* **Modeller > Frigör utrymme**.
- *En enhet:* **Glöm** (Enheter på den här datorn) eller **Ta bort** (Inställningar).
- *En apps åtkomst:* **Ta bort åtkomst** (Enheter och appar).
- *Allt:* avinstallera appen. En egen modellmapp som du valt lämnas kvar; ta bort mappen `Portal AI Host Models` själv om du vill att den ska försvinna.

Appen tar aldrig bort något den inte själv skapat, skriver aldrig över dina filer, och en modell du själv lagt till tas bort endast som värdtjänstens egen kopia (din originalfil påverkas inte).

---

## 16. Kortkommandon

| Tangenter | Åtgärd |
|---|---|
| Ctrl+1 | Start |
| Ctrl+2 | Modeller |
| Ctrl+3 | Enheter och appar |
| Ctrl+4 | Prova |
| Ctrl+, | Inställningar |
| F1 | Om |
| Ctrl+Retur | Fråga, på Prova |
| Esc | Stänger Nyheter eller en dialogruta |

Listan finns också under appmenyn > **Kortkommandon**.

Utseende: **Inställningar > Utseende** erbjuder Tema (standard: din Windows-inställning), Fönstermaterial (Mica), Hörn (Rundade), Täthet (Standard), Textstorlek (Normal) och Rörelse (Följ Windows). Windows högkontrastläge använder alltid enfärgade systemfärger.

---

## 17. Felsökning och vanliga frågor

**Värdtjänsten säger att den inte körs.** Tryck på **Starta Portal AI Host** på Start. Om den inte kan starta, starta om datorn; om det misslyckas, installera om från Store.

**"En annan Windows-användare på den här datorn kör redan Portal AI Host."** Be den användaren att avsluta den, eller logga in som den användaren.

**Svaren är långsamma.** Se avsnitt 7. En modell som är för stor för ditt videominne körs delvis på processorn.

**En app säger att ingen AI är tillgänglig.** Kontrollera att appen är tillåten (Enheter och appar), inte pausad, och att en modell är installerad som inte är för stor för datorn.

**Det första svaret är långsamt, de följande är snabba.** Modellen vilade. Den läses in vid första förfrågan. Välj **Håll modellen inläst** i Inställningar > Allmänt för att undvika detta.

**Skickas mina data någonstans?** Nej. Frågor och svar stannar på dina enheter. Endast kraschrapporter kan lämna datorn (avsnitt 14), och du kan stänga av dem.

**Kan jag använda den utan internet?** Ja, när en modell är nedladdad.

**Kostar det något?** Nej.

**Vilken modell ska jag välja?** Den rekommenderade. Välj en mindre om din dator känns långsam, en större om du vill ha bättre svar och har minnet.

**Nedladdningen fastnade på "Kontrollerar nedladdningen".** Vänta. Stora filer tar en minut att kontrollera.

**Parkopplingen säger "för många enheter".** Gränsen är 16 enheter. Ta bort en.

**Var är ikonen i aktivitetsfältet?** Klicka på pilen bredvid klockan för att visa dolda ikoner.

Om du fortfarande kör fast, använd **Om > Kopiera version**, skriv till mindthaapp@gmail.com och klistra in versionen i ditt meddelande.

---

## 18. Kommer senare

Det här är planerat och finns inte i appen ännu.

- **Modellkategorier på sidan Modeller:** grupperade efter uppgift (chatt och skrivande, kodning, resonemang, syn, inbäddningar, tal), med filter och sök.
- **Bild- och videogenerering** på den här datorn, inklusive en "regissör" som kedjar scener till en video. Videomodeller visas som "Kommer snart" tills motorn finns.
- **Att välja vilken modell som svarar**, och modeller som ber varandra om hjälp, i AI Portal.
- **macOS-version.**

---

## 19. Support

- **Feedback och idéer:** Om > **Skicka feedback eller föreslå en idé**.
- **Support- och integritetsfrågor:** mindthaapp@gmail.com.
- **Version:** Om visar den, och **Kopiera version** lägger den på urklipp för ditt meddelande.
- **Licenser:** Portal AI Host innehåller llama.cpp (MIT-licens), som kör modellerna på din dator. Modeller laddas ner under sina egna licenser (Apache 2.0 eller MIT). Se Om.
