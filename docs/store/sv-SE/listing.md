# Butiksbeskrivning (svenska, sv-SE)

Översättning av den engelska butiksbeskrivningen (listing-en.md). Endast de avsnitt som användare ser. Produktnamn och länkar är oförändrade.

## Appnamn

Portal AI Host

## Kort beskrivning

Kör en lokal AI-modell på din egen dator och dela den med dina Portal-appar. Ett klick väljer den bästa modellen för din hårdvara, laddar ner den och håller den redo. Din text stannar på den här datorn: inget konto, inget moln, ingen prenumeration.

## Beskrivning

Portal AI Host ger din dator en egen AI-modell och låter dina andra appar använda den.

I stället för att varje app laddar ner sin egen modell, eller skickar din text till en molntjänst, behåller Portal AI Host en modell på din dator och delar den med de appar du tillåter. Voice Portal och de andra Portal-apparna kan då skriva sammanfattningar, protokoll och svar med en större och bättre modell än de skulle kunna bära på egen hand.

Enkel att komma igång med
- Vid första starten kontrollerar appen din dator (minne, grafikkort, ledigt diskutrymme) och rekommenderar den bästa modellen som fungerar bra på den, med en tydlig förklaring.
- Ett klick laddar ner den. Nedladdningar kan pausas och återupptas, och varje fil kontrolleras innan den används.
- Inget annat att installera: motorn ingår. Ingen Python, ingen kommandorad, inga extra verktyg.

Privat i sin konstruktion
- Modellen körs på den här datorn. Frågor och svar lämnar aldrig datorn.
- Inget konto och ingen inloggning.
- Inga analyser. Valfria kraschrapporter innehåller aldrig dina filer eller det du skriver, och du kan stänga av dem. Dina frågor sparas eller loggas aldrig.
- Värdtjänsten lyssnar endast på den här datorn (loopback). Andra datorer i ditt nätverk kan inte nå den.

Du behåller kontrollen
- En app måste fråga innan den kan använda värdtjänsten, och du bestämmer: tillåt, neka eller besluta senare. Du kan när som helst ta bort en apps åtkomst.
- Värdtjänsten visar om en app är verifierad (dess utgivare), så att du vet vem som frågar.
- Välj var modeller lagras och ta bort de modeller du inte längre behöver. Värdtjänsten tar bara bort filer som den själv har laddat ner.
- Starta med Windows är valfritt och avstängt tills du slår på det.

Fungerar med din hårdvara
- Använder ditt grafikkort via Vulkan (AMD, NVIDIA och Intel) och faller tillbaka på processorn när det inte finns något lämpligt grafikkort.
- Frigör minne när modellen är inaktiv, eller håller den laddad om du föredrar det.

För utvecklare
- Värdtjänsten erbjuder ett standard-API som är kompatibelt med OpenAI på 127.0.0.1 (chat completions med strömning, inbäddningar och en modellista), så att befintliga klientbibliotek fungerar. Varje app behöver först användarens godkännande.

Modellerna laddas ner från utgivarnas sidor på Hugging Face. Varje modell har sin egen licens; appen erbjuder bara modeller vars licenser tillåter kommersiell användning (Apache 2.0 eller MIT).

## Funktioner (Partner Center, "Produktfunktioner", högst 20, 200 tecken vardera)

1. Kör en AI-modell lokalt på din dator, inget moln behövs
2. En modell som delas av alla dina Portal-appar
3. Rekommenderar den bästa modellen för din hårdvara
4. Nedladdning med ett klick, med paus, återupptagning och filkontroll
5. Använder ditt grafikkort (Vulkan), med automatisk reserv på processorn
6. Inget konto, ingen inloggning, ingen prenumeration
7. Inga analyser; kraschrapporter är valfria och rensade; prompter sparas aldrig eller loggas
8. Lyssnar endast på den här datorn (loopback)
9. Appar måste fråga först; du tillåter, nekar eller tar bort åtkomst
10. Visar om en frågande app är verifierad
11. Frigör minne när den är inaktiv, eller håller modellen laddad
12. Välj mappen för dina modeller
13. Valfri start med Windows, med en ikon i aktivitetsfältet
14. Rutan "Prova" för att kontrollera att modellen fungerar
15. Lokalt API som är kompatibelt med OpenAI för utvecklare
16. Rättvis fördelning när flera appar frågar samtidigt
17. Återhämtar sig själv om motorn stannar

## Sökord (nyckelord, 7)

1. lokal AI
2. offline-AI
3. privat AI
4. LLM
5. AI-modell
6. språkmodell
7. AI-server

Inga andra produkters namn som sökord (butikspolicy 10.1.3).

## Nytt i den här versionen (1.0)

Första versionen. Ladda ner en lokal AI-modell med ett klick och dela den privat med dina Portal-appar, på din egen dator.

## Systemkrav (text i butiksbeskrivningen)

> Fungerar bäst med 16 GB minne och ett grafikkort med 8 GB videominne. Utan ett lämpligt grafikkort körs modellen på processorn, vilket är långsammare. Arm64-datorer kör modellen på processorn. Modellerna behöver mellan 2 och 20 GB ledigt diskutrymme.

Minimum: Windows 11 (version 21H2, build 22000) eller senare, x64 eller Arm64. Minne: 8 GB. Ledigt diskutrymme: 5 GB (appen i sig är liten; modellerna är 2 till 20 GB, och större valfria modeller upp till cirka 140 GB).

Rekommenderat: 16 GB minne eller mer (32 GB eller mer för de stora modellerna). Ett Vulkan-kompatibelt grafikkort med 8 GB videominne eller mer (AMD, NVIDIA eller Intel). 25 GB ledigt diskutrymme.
