# Store-Eintrag (Deutsch, de-DE)

Übersetzung des englischen Store-Eintrags (listing-en.md). Nur die nutzersichtbaren Abschnitte. Produktnamen und Links bleiben unverändert.

## App-Name

Portal AI Host

## Kurzbeschreibung

Betreibe ein lokales KI-Modell auf deinem eigenen PC und teile es mit deinen Portal-Apps. Ein Klick wählt das beste Modell für deine Hardware, lädt es herunter und hält es bereit. Dein Text bleibt auf diesem PC: kein Konto, keine Cloud, kein Abo.

## Beschreibung

Portal AI Host gibt deinem PC ein eigenes KI-Modell und lässt deine anderen Apps es nutzen.

Statt dass jede App ihr eigenes Modell herunterlädt oder deinen Text an einen Cloud-Dienst schickt, behält Portal AI Host ein Modell auf deinem PC und teilt es mit den Apps, die du erlaubst. Voice Portal und die anderen Portal-Apps können damit Zusammenfassungen, Protokolle und Antworten mit einem größeren, besseren Modell erstellen, als sie allein tragen könnten.

Einfach einzurichten
- Beim ersten Start prüft die App deinen PC (Arbeitsspeicher, Grafikkarte, freier Speicherplatz) und empfiehlt das beste Modell, das darauf gut läuft, mit einer verständlichen Erklärung.
- Ein Klick lädt es herunter. Downloads lassen sich anhalten und fortsetzen, und jede Datei wird geprüft, bevor sie verwendet wird.
- Nichts weiter zu installieren: Die Engine ist integriert. Kein Python, keine Kommandozeile, keine zusätzlichen Werkzeuge.

Privat by design
- Das Modell läuft auf diesem PC. Eingaben und Antworten verlassen ihn nicht.
- Kein Konto und keine Anmeldung.
- Keine Analysedaten. Optionale Absturzberichte enthalten nie deine Dateien oder deine Eingaben, und du kannst sie abschalten. Deine Eingaben werden nie gespeichert oder protokolliert.
- Der Host lauscht nur auf diesem PC (Loopback). Andere Computer in deinem Netzwerk können ihn nicht erreichen.

Du behältst die Kontrolle
- Eine App muss fragen, bevor sie den Host nutzen darf. Du entscheidest: erlauben, ablehnen oder später entscheiden. Den Zugriff einer App kannst du jederzeit entfernen.
- Der Host zeigt, ob eine App verifiziert ist (ihr Herausgeber), damit du weißt, wer fragt.
- Wähle, wo Modelle gespeichert werden, und entferne Modelle, die du nicht mehr brauchst. Der Host löscht nur Dateien, die er selbst heruntergeladen hat.
- Start mit Windows ist optional und bleibt aus, bis du es einschaltest.

Funktioniert mit deiner Hardware
- Nutzt deine Grafikkarte über Vulkan (AMD, NVIDIA und Intel) und greift auf den Prozessor zurück, wenn keine geeignete Grafikkarte vorhanden ist.
- Gibt den Speicher frei, wenn das Modell untätig ist, oder hält es geladen, falls dir das lieber ist.

Für Entwickler
- Der Host bietet eine OpenAI-kompatible Standard-API auf 127.0.0.1 (Chat Completions mit Streaming, Embeddings und eine Modellliste), sodass vorhandene Client-Bibliotheken funktionieren. Jede App braucht zuerst die Zustimmung des Nutzers.

Modelle werden von den Seiten ihrer Herausgeber auf Hugging Face heruntergeladen. Jedes Modell hat seine eigene Lizenz. Die App bietet nur Modelle an, deren Lizenz die kommerzielle Nutzung erlaubt (Apache 2.0 oder MIT).

## Funktionen (Partner Center „Produktfunktionen“, max. 20, je 200 Zeichen)

1. Führt ein KI-Modell lokal auf deinem PC aus, ohne Cloud
2. Ein Modell, von allen deinen Portal-Apps gemeinsam genutzt
3. Empfiehlt das beste Modell für deine Hardware
4. Ein-Klick-Download mit Anhalten, Fortsetzen und Dateiprüfung
5. Nutzt deine Grafikkarte (Vulkan), mit automatischem Rückgriff auf den Prozessor
6. Kein Konto, keine Anmeldung, kein Abo
7. Keine Analysedaten; Absturzberichte sind optional und bereinigt; Eingaben werden nie gespeichert oder protokolliert
8. Lauscht nur auf diesem PC (Loopback)
9. Apps müssen zuerst fragen; du erlaubst, lehnst ab oder entfernst den Zugriff
10. Zeigt, ob eine anfragende App verifiziert ist
11. Gibt Speicher frei, wenn untätig, oder hält das Modell geladen
12. Wähle den Ordner für deine Modelle
13. Optionaler Start mit Windows, mit Symbol im Infobereich
14. Testfeld „Ausprobieren“, um zu prüfen, dass das Modell funktioniert
15. OpenAI-kompatible lokale API für Entwickler
16. Faire Verteilung, wenn mehrere Apps gleichzeitig fragen
17. Erholt sich selbstständig, wenn die Engine stoppt

## Suchbegriffe (Schlüsselwörter, 7)

1. lokale KI
2. Offline-KI
3. private KI
4. LLM
5. KI-Modell
6. Sprachmodell
7. KI-Server

Keine Namen fremder Produkte als Schlüsselwörter (Store-Richtlinie 10.1.3).

## Neu in dieser Version (1.0)

Erste Version. Lade mit einem Klick ein lokales KI-Modell herunter und teile es privat auf deinem eigenen PC mit deinen Portal-Apps.

## Systemanforderungen (Listentext)

> Funktioniert am besten mit 16 GB Arbeitsspeicher und einer Grafikkarte mit 8 GB Videospeicher. Ohne geeignete Grafikkarte läuft das Modell auf dem Prozessor, was langsamer ist. Arm64-PCs führen das Modell auf dem Prozessor aus. Modelle benötigen 2 bis 20 GB freien Speicherplatz.

Minimum: Windows 11 (Version 21H2, Build 22000) oder neuer, x64 oder Arm64. Arbeitsspeicher 8 GB. Freier Speicherplatz 5 GB (die App selbst ist klein; Modelle sind 2 bis 20 GB groß, größere optionale Modelle bis etwa 140 GB).

Empfohlen: 16 GB Arbeitsspeicher oder mehr (32 GB oder mehr für die großen Modelle). Eine Vulkan-fähige Grafikkarte mit 8 GB Videospeicher oder mehr (AMD, NVIDIA oder Intel). 25 GB freier Speicherplatz.
