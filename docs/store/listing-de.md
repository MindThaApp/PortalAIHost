# Store-Eintrag (Deutsch, de-de)

Platzhalter und Notizen: Diese Texte in Partner Center > Einreichung > Store-Einträge > Deutsch einfügen. Sie sind eine Übersetzung von listing-en.md und müssen vor der Einreichung von einer Muttersprachlerin oder einem Muttersprachler geprüft werden. Zertifizierungsnotizen, Altersfreigabe und Berechtigungsbegründung bleiben auf Englisch (nur in listing-en.md).

## App-Name

Portal AI Host

## Kurzbeschreibung

Betreiben Sie ein lokales KI-Modell auf Ihrem eigenen PC und teilen Sie es mit Ihren Portal-Apps. Ein Klick wählt das beste Modell für Ihre Hardware, lädt es herunter und hält es bereit. Ihr Text bleibt auf diesem PC: kein Konto, keine Cloud, kein Abo.

## Beschreibung

Portal AI Host gibt Ihrem PC ein eigenes KI-Modell und lässt Ihre anderen Apps es nutzen.

Statt dass jede App ihr eigenes Modell herunterlädt oder Ihren Text an einen Cloud-Dienst sendet, hält Portal AI Host ein Modell auf Ihrem PC bereit und teilt es mit den Apps, die Sie zulassen. Voice Portal und die anderen Portal-Apps können damit Zusammenfassungen, Protokolle und Antworten mit einem größeren, besseren Modell schreiben, als sie allein tragen könnten.

Einfach einzurichten
- Beim ersten Start wird Ihr PC geprüft (Arbeitsspeicher, Grafikkarte, freier Speicherplatz) und das beste Modell empfohlen, das darauf gut läuft, mit einer verständlichen Erklärung.
- Ein Klick lädt es herunter. Downloads können angehalten und fortgesetzt werden, und jede Datei wird vor der Verwendung geprüft.
- Nichts weiter zu installieren: Die Engine ist eingebaut. Kein Python, keine Befehlszeile, keine zusätzlichen Tools.

Privat von Grund auf
- Das Modell läuft auf diesem PC. Eingaben und Antworten verlassen ihn nie.
- Kein Konto und keine Anmeldung.
- Keine Analysen. Optionale Absturzberichte enthalten nie Ihre Dateien oder Eingaben und lassen sich abschalten. Ihre Eingaben werden nie gespeichert oder protokolliert.
- Der Host lauscht nur auf diesem PC (Loopback). Andere Computer in Ihrem Netzwerk erreichen ihn nicht.

Sie behalten die Kontrolle
- Eine App muss erst anfragen, bevor sie den Host nutzen darf, und Sie entscheiden: zulassen, ablehnen oder später entscheiden. Sie können einer App den Zugriff jederzeit entziehen.
- Der Host zeigt, ob eine App verifiziert ist (ihr Herausgeber) oder nicht, damit Sie wissen, wer anfragt.
- Wählen Sie, wo Modelle gespeichert werden, und entfernen Sie Modelle, die Sie nicht mehr brauchen. Der Host löscht nur Dateien, die er selbst heruntergeladen hat.
- „Mit Windows starten“ ist optional und bleibt aus, bis Sie es einschalten.

Passt zu Ihrer Hardware
- Nutzt Ihre Grafikkarte über Vulkan (AMD, NVIDIA und Intel) und weicht auf den Prozessor aus, wenn keine passende GPU vorhanden ist.
- Gibt Speicher frei, wenn das Modell im Leerlauf ist, oder hält es geladen, wenn Sie das bevorzugen.

Für Entwickler
- Der Host bietet eine standardkonforme, OpenAI-kompatible API auf 127.0.0.1 (Chat-Vervollständigungen mit Streaming, Embeddings und eine Modellliste), sodass vorhandene Client-Bibliotheken funktionieren. Jede App braucht zuerst die Zustimmung des Nutzers.

Modelle werden von den Seiten ihrer Herausgeber auf Hugging Face heruntergeladen. Jedes Modell hat seine eigene Lizenz; die App bietet nur Modelle an, deren Lizenzen kommerzielle Nutzung erlauben (Apache 2.0 oder MIT).

## Funktionen (höchstens 20, je 200 Zeichen)

1. Führt ein KI-Modell lokal auf Ihrem PC aus, ganz ohne Cloud
2. Ein Modell für alle Ihre Portal-Apps
3. Empfiehlt das beste Modell für Ihre Hardware
4. Download mit einem Klick, mit Pause, Fortsetzen und Dateiprüfung
5. Nutzt Ihre Grafikkarte (Vulkan) mit automatischem CPU-Ersatz
6. Kein Konto, keine Anmeldung, kein Abo
7. Keine Analysen; Absturzberichte sind optional und bereinigt; Eingaben werden nie gespeichert oder protokolliert
8. Lauscht nur auf diesem PC (Loopback)
9. Apps müssen zuerst fragen; Sie lassen zu, lehnen ab oder entziehen den Zugriff
10. Zeigt, ob eine anfragende App verifiziert ist
11. Gibt Speicher im Leerlauf frei oder hält das Modell geladen
12. Ordner für Ihre Modelle frei wählbar
13. Optionaler Start mit Windows, mit Taskleistensymbol
14. Testfeld „Ausprobieren“, um zu prüfen, ob das Modell funktioniert
15. OpenAI-kompatible lokale API für Entwickler
16. Faire Verteilung, wenn mehrere Apps gleichzeitig anfragen
17. Erholt sich selbstständig, wenn die Engine stoppt

## Suchbegriffe (7)

1. lokale KI
2. Offline-KI
3. private KI
4. LLM
5. KI-Modell
6. Sprachmodell
7. KI-Server

## Neu in dieser Version (1.0)

Erste Version. Laden Sie mit einem Klick ein lokales KI-Modell herunter und teilen Sie es privat auf Ihrem eigenen PC mit Ihren Portal-Apps.

## Systemanforderungen (Zusatztext)

> Funktioniert am besten mit 16 GB Arbeitsspeicher und einer Grafikkarte mit 8 GB Videospeicher. Ohne passende Grafikkarte läuft das Modell auf dem Prozessor, was langsamer ist. Arm64-PCs führen das Modell auf dem Prozessor aus. Modelle benötigen 2 bis 20 GB freien Speicherplatz.

## Kategorie

Produktivität
