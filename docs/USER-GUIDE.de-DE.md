# Portal AI Host: Benutzerhandbuch

Portal AI Host betreibt ein KI-Modell auf deinem PC und teilt es mit deinen Portal-Apps. Deine Fragen und Dateien bleiben auf deinen eigenen Geräten. Dieses Handbuch erklärt jeden Teil der App, was ihre Einstellungen bewirken und was zu tun ist, wenn etwas schiefgeht.

Stellen, an denen ein Bild hingehört, sind mit `[screenshot: ...]` markiert.

## Inhalt

1. Schnellstart
2. Systemanforderungen, Installation, Update, Deinstallation
3. Sich in der App zurechtfinden
4. Modelle
5. Apps und Berechtigungen
6. Ausprobieren
7. Leistungseinstellungen
8. Benachrichtigungen
9. Andere Geräte diesen PC nutzen lassen
10. Geräte an diesem PC
11. Modellsteuerung und zwei Modelle gleichzeitig
12. Den Heim-PC vom Laptop aus nutzen (Client-Modus)
13. Unterwegs: das Relais und der Relais-Zugangsschlüssel
14. Datenschutz und Absturzberichte
15. Wo deine Daten gespeichert sind, Sicherung und Entfernung
16. Tastenkombinationen
17. Fehlerbehebung und FAQ
18. Kommt später
19. Support

---

## 1. Schnellstart

1. Installiere Portal AI Host aus dem Microsoft Store und öffne es.
2. Das Willkommensfenster prüft deinen PC (Arbeitsspeicher, Grafikkarte, freier Speicherplatz) und empfiehlt ein Modell. Wähle **Mit empfohlenen Einstellungen starten** oder **Herunterladen**, um das Modell selbst auszuwählen. **Später wählen** überspringt diesen Schritt.
3. Suche das Symbol neben der Uhr im Infobereich. Portal AI Host wartet dort still. Klicke darauf, um einen kurzen Status zu sehen.
4. Öffne **Ausprobieren** und stelle eine kurze Frage, um zu prüfen, dass das Modell funktioniert.
5. Wenn eine Portal-App zum ersten Mal KI nutzen möchte, fragt dich ein Hinweis, ob du es erlauben oder ablehnen willst.

[screenshot: das Willkommensfenster mit dem empfohlenen Modell]

Wenn stattdessen die KI deines Heim-PCs auf einem Laptop genutzt werden soll, wähle im Willkommensfenster **Heim-PC nutzen**. Siehe Abschnitt 12.

**Kostenlos oder kostenpflichtig:** Portal AI Host ist kostenlos. Für keine Funktion in diesem Handbuch sind ein Konto, eine Anmeldung oder ein Kauf nötig.

---

## 2. Systemanforderungen, Installation, Update, Deinstallation

### Anforderungen

| | Minimum | Empfohlen |
|---|---|---|
| Windows | Windows 11 (21H2, Build 22000) oder neuer, x64 oder Arm64 | Dasselbe |
| Arbeitsspeicher | 8 GB | 16 GB oder mehr (32 GB oder mehr für die großen Modelle) |
| Grafik | Keine (das Modell läuft dann auf dem Prozessor, was langsamer ist) | Eine Vulkan-Grafikkarte mit 8 GB Videospeicher oder mehr (AMD, NVIDIA oder Intel) |
| Freier Speicherplatz | 5 GB | 25 GB |

Modelle sind jeweils 2 bis 20 GB groß. Arm64-PCs führen das Modell auf dem Prozessor aus. Eine Internetverbindung wird nur zum Herunterladen von Modellen und für das Relais benötigt. Sobald ein Modell auf deinem PC ist, funktioniert es offline.

### Installation

Installiere die App aus dem Microsoft Store. Mehr ist nicht nötig: kein Python, keine Kommandozeile, keine andere Software. Die KI-Engine ist Teil der App.

### Start mit Windows

**Einstellungen > Allgemein > Start mit Windows** (Standard: aus, außer beim Willkommensfenster, wo „Mit empfohlenen Einstellungen starten“ es einschaltet). Ist es an, wartet Portal AI Host nach der Anmeldung im Infobereich, damit Apps sofort darauf zugreifen können.
- Wenn der Schalter sagt, dass er nur in der installierten App verfügbar ist, läufst du eine Entwicklungskopie.
- Wenn du es im Task-Manager (Autostart-Apps) ausgeschaltet hast, schalte es dort wieder ein.
- Wenn deine Organisation es festlegt, ist der Schalter gesperrt.

### Update

Der Store aktualisiert die App. Nach einem Update öffnet sich **Neuigkeiten** einmal. Du kannst es über das App-Menü erneut öffnen. Modelle bleiben über Updates hinweg an ihrem Platz.

### Deinstallation

Entferne die App unter **Windows-Einstellungen > Apps**. Modelle im Standardordner werden im eigenen Datenordner der App gespeichert und mit ihr entfernt. Ein selbst gewählter Modellordner bleibt unberührt. Siehe Abschnitt 15, bevor du deinstallierst, wenn du etwas behalten willst.

---

## 3. Sich in der App zurechtfinden

Auf der linken Seite gibt es fünf Seiten.

| Seite | Wofür sie da ist | Tastenkürzel |
|---|---|---|
| Start | Status, das aktive Modell, Speichernutzung, die App, die gerade KI nutzt | Strg+1 |
| Modelle | Modelle herunterladen, nutzen und entfernen | Strg+2 |
| Geräte und Apps | Portal-Apps erlauben oder entfernen, pausieren, Priorität festlegen | Strg+3 |
| Ausprobieren | Dem Modell eine kurze Frage stellen | Strg+4 |
| Einstellungen | Alles, was sich konfigurieren lässt | Strg+, |

Eine sechste Seite, **Geräte dieses PCs**, listet die Laptops und Telefone auf, die mit diesem PC gekoppelt sind (siehe Abschnitt 10). Das App-Menü enthält Einstellungen, Tastenkombinationen, Neuigkeiten und Info.

**Start** zeigt, ob die KI **Bereit**, **Beantwortet gerade**, **Wird vorbereitet**, **Ruht** (das Modell wacht auf, wenn eine App fragt) oder noch **kein Modell hat**. **AKTUELL** zeigt, welche App gerade eine Antwort bekommt und ungefähr wie viele Wörter pro Sekunde entstehen. Außerdem zeigt es, wie viel Arbeitsspeicher und Grafikspeicher das Modell nutzt, sowie eine Schaltfläche **Jetzt laden** / **Jetzt entladen**. **HEUTE** zählt die Anfragen jeder App. Eine Gesundheitskarte zeigt „Alles in Ordnung“ oder listet auf, was Aufmerksamkeit braucht, jeweils mit einer Ein-Klick-Lösung wie Modell laden, Engine neu starten, Dateien prüfen oder Leistungseinstellungen öffnen.

[screenshot: Seite „Start“, während eine App das Modell nutzt]

Das Symbol im Infobereich öffnet ein kleines Fenster mit Modell, Speicher, letzter Anfrage und Warteschlange, mit den Schaltflächen **App öffnen** und **Modell entladen**.

[screenshot: das Fenster am Infobereich-Symbol]

Wenn das Host-Programm nicht läuft, bietet die Startseite **Portal AI Host starten** an. Wenn es mehr als einmal unerwartet beendet wurde, zeigt eine Leiste **Abgesicherter Modus** an, dass es mit vorsichtigen Einstellungen gestartet wurde. Deine Apps funktionieren weiterhin. Starte den PC neu, falls das immer wieder passiert.

---

## 4. Modelle

Ein Modell ist die KI, die die Arbeit erledigt. **Modelle** (Strg+2) zeigt, was du hast und was du hinzufügen kannst.

### Empfohlenes Modell

Der erste Start und die Startseite empfehlen „das beste Modell für diesen PC“, mit einer verständlichen Begründung, basierend auf deinem Arbeitsspeicher, Grafikspeicher und freien Speicherplatz. Sobald ein besseres verfügbar ist, zeigt eine Leiste **Ein besseres Modell ist verfügbar**. Dein aktuelles Modell bleibt, bis das neue bereit ist, und du entscheidest, wann das alte entfernt wird.

### Modell herunterladen

1. Öffne **Modelle**. Wähle ein Modell unter **Modelle, die du hinzufügen kannst**.
2. Drücke **Herunterladen**. Eine Leiste zeigt Fortschritt und verbleibende Zeit.
3. **Anhalten** und **Fortsetzen** sind jederzeit möglich. **Beenden** verwirft den bereits heruntergeladenen Teil (nach einer Bestätigung).
4. Der Host prüft die heruntergeladenen Dateien, bevor er sie verwendet.
5. Wenn es fertig ist, drücke **Dieses Modell verwenden**.

Downloads lassen sich nach einem Neustart fortsetzen und werden Datei für Datei geprüft. Ein Download braucht genug freien Speicherplatz; wenn nicht, zeigt die Seite, wie viel fehlt, und bietet **Ordner in den Einstellungen ändern** an. Der PC bleibt wach, solange ein Download läuft (Einstellungen > Leistung, Standard: an).

[screenshot: Seite „Modelle“ mit einem laufenden Download]

Ein Modell mit dem Hinweis „Dieses Modell ist für diesen PC möglicherweise zu groß“ kann trotzdem heruntergeladen werden, läuft aber womöglich langsam oder passt nicht.

### Details, Dateien prüfen, erneut laden, entfernen

- **Details** zeigt, wofür das Modell gut ist, was zu beachten ist, wie viel Speicher es braucht, seine Lizenz, wann es zuletzt genutzt wurde und wie viel Platz es belegt.
- **Dateien prüfen** prüft ein Modell auf dem Datenträger erneut. Dabei wird nie etwas gelöscht. Ein beschädigtes Modell wird als **Beschädigt** markiert.
- **Erneut laden** (bei einem beschädigten Modell) entfernt die beschädigte Kopie und lädt sie neu herunter. Sonst wird nichts gelöscht.
- **Entfernen** nimmt ein Modell von diesem PC, nach einer Bestätigung, die angibt, wie viel Platz frei wird. Ist das Modell in Gebrauch, entlädt der Host es zuerst und fragt, was als Nächstes verwendet werden soll. Gelöscht werden nur Dateien, die der Host heruntergeladen hat.

### Eigenes Modell hinzufügen

**Eigenes Modell hinzufügen...** lässt dich eine `.gguf`-Datei auswählen. Der Host legt eine eigene Kopie im Modellordner an. Dein Original wird nie verändert oder verschoben. Die Zeile wird mit **Von dir hinzugefügt** markiert, und der Host kann den Speicherbedarf nicht abschätzen. Probiere es also aus. **Entfernen** bei einem solchen Modell löscht nur die Kopie des Hosts.

### Speicherplatz freigeben

**Speicherplatz freigeben** listet Überbleibsel auf, die die App selbst erzeugt hat: unvollständige Downloads, die nicht fortgesetzt werden, alte Kopien nach dem Verschieben deiner Modelle und Modelle, die eine Prüfung als beschädigt erkannt hat. Du bestätigst, bevor etwas entfernt wird. Modelle, die du nutzt, und Dateien, die du selbst hinzugefügt hast, werden nie aufgelistet. Unvollständige Downloads, die du noch fortsetzen kannst, bleiben eine Woche erhalten.

### Modellordner und Verschieben deiner Modelle

**Einstellungen > Modellordner** legt fest, wohin neue Downloads gehen. Lass das Feld leer für den Standardordner, oder gib einen vollständigen Pfad wie `D:\Modelle` ein und drücke die Eingabetaste. Bereits heruntergeladene Modelle werden nicht verschoben oder gelöscht. Sie bleiben, wo sie sind, und bleiben aufgelistet. Um sie zu verschieben, akzeptiere **Meine Modelle dorthin verschieben**. Sie werden kopiert, geprüft und erst danach werden die alten Kopien entfernt. Das gerade genutzte Modell wird dabei entladen, und **Verschieben abbrechen** lässt alles dort, wo es war.

### Speicherbudget und Balken

Die Balken **Speicherbudget** auf der Seite Modelle zeigen Arbeits- und Grafikspeicher: wie viel die geladenen Modelle nutzen, wie viel beim Laden eines Modells reserviert wird und wie viel gegenüber deinen Grenzwerten frei ist (siehe Abschnitt 7). Ein Laden, das dein Speicherlimit überschreiten würde, wird mit einem verständlichen Grund abgelehnt, bevor etwas gestoppt wird.

[screenshot: die Balken „Speicherbudget“ mit zwei geladenen Modellen]

### Platzierung: Grafikkarte, Arbeitsspeicher oder nur Prozessor

Wo ein Modell läuft, legt **Einstellungen > Leistung > Ausführen auf** fest (Automatisch, Grafikkarte, Prozessor). Ein Modell, das nicht auf die Grafikkarte passt, läuft teilweise auf dem Prozessor (langsamer). Siehe auch die Platzierung pro Modell in Abschnitt 11, die **Nur Prozessor** enthält.

### Grenzen sowie kostenlos oder kostenpflichtig

- Alle Modelle in der Liste sind für die kommerzielle Nutzung kostenlos (Lizenzen Apache 2.0 oder MIT). Jedes Modell zeigt seine eigene Lizenz.
- Modelle werden von den Seiten ihrer Herausgeber auf Hugging Face heruntergeladen. Der Host akzeptiert nur eine vertrauenswürdige, signierte Modellliste. Kann er sie nicht laden, zeigt eine Leiste **Modellliste nicht verfügbar**. Modelle, die bereits auf diesem PC sind, funktionieren weiterhin.

### Fehlerbehebung

- *Keine Modelle zum Herunterladen aufgelistet:* Prüfe deine Internetverbindung und drücke auf der Startseite **Erneut versuchen**.
- *Download bleibt stehen:* Drücke **Fortsetzen**. Ist der freie Speicherplatz knapp, gib Platz frei oder ändere den Ordner.
- *Modell „ist möglicherweise zu groß“:* Wähle ein kleineres oder verringere die Last unter Einstellungen > Leistung.

---

## 5. Apps und Berechtigungen

Öffne **Geräte und Apps** (Strg+3).

- **Wartet auf deine Antwort:** Eine App, die KI nutzen möchte, erscheint hier und als Hinweis („Soll Voice Portal KI auf diesem PC nutzen dürfen?“). Wähle **Erlauben**, **Nicht erlauben** oder **Später entscheiden**. Der Hinweis zeigt, ob die App eine verifizierte Portal-App, eine installierte App, die keine Portal-App ist, oder nicht verifiziert ist (dann nennt er, woher sie gestartet wird). Erlaube sie nur, wenn du sie selbst gestartet hast.
- **Apps, die deinen KI-Host nutzen können:** Jede erlaubte App zeigt ihre Anfragen heute und wann sie zuletzt genutzt wurde.
  - **Pausieren** lehnt neue Anfragen ab, bis du fortsetzt. Die App behält ihren Zugriff.
  - **Priorität** (Hoch, Normal, Niedrig) bestimmt, wer zuerst drankommt, wenn mehrere Apps gleichzeitig fragen. Interaktive Anfragen kommen vor Stapelarbeit, und eine Anfrage, die lange wartet, rückt nach vorn, damit eine Niedrig-Priorität-App nie leer ausgeht.
  - **Zugriff entfernen** stoppt die App, bis du sie wieder erlaubst. **Rückgängig** erscheint kurz.

Apps müssen fragen, bevor sie den Host nutzen können. Der Host lauscht nur auf diesem PC, es sei denn, du schaltest die Freigabe für andere Geräte ein (Abschnitt 9).

[screenshot: Seite „Geräte und Apps“ mit einer wartenden und einer erlaubten App]

Für Entwickler: Der Host bietet eine OpenAI-kompatible lokale API unter `http://127.0.0.1:41870` (Chat Completions mit Streaming, Embeddings, Modellliste). Siehe `api-v1.md`.

**Wo es gespeichert wird:** Erlaubte Apps und ihre Tokens werden im Datenordner des Hosts gespeichert, geschützt für deinen Windows-Benutzer. Wenn du den Zugriff einer App entfernst, wird ihr Token entfernt.

---

## 6. Ausprobieren

**Ausprobieren** (Strg+4) prüft, ob das Modell funktioniert. Gib eine kurze Frage ein und drücke **Fragen** (oder Strg+Eingabe). **Beenden** stoppt die Antwort, **Leeren** leert die Seite. Es werden Vorschläge für Fragen angeboten. Die Seite zeigt, wie lange die ersten Wörter gebraucht haben, und die Geschwindigkeit in Tokens pro Sekunde. Nichts, was du hier eingibst, wird gespeichert.

[screenshot: Ausprobieren mit einer Antwort und der Zeitangabe]

---

## 7. Leistungseinstellungen

**Einstellungen > Leistung** sorgt dafür, dass die KI deinen PC nicht ausbremst. Änderungen wirken sofort. Ein Modell, das nicht mehr passt, wird mit den neuen Grenzwerten neu geladen oder entladen (es bleibt auf dem Datenträger). **Auf Standard zurücksetzen** stellt den Abschnitt wieder her.

| Einstellung | Was sie bewirkt | Standard |
|---|---|---|
| Leistung | Eine Voreinstellung: **Leise** (etwa halbe Geschwindigkeit, der PC bleibt reaktionsschnell), **Ausgewogen** (gute Geschwindigkeit, deine Programme haben Vorrang), **Schnell** (schnellste Antworten, andere Programme können langsamer werden), **Benutzerdefiniert** | Ausgewogen |
| Maximaler Speicher für KI | Die KI verwendet nie mehr Speicher als das | 80 % des Speichers des PCs |
| Immer frei lassen | Speicher, der für deine anderen Programme frei bleibt | Durch die Voreinstellung festgelegt |
| Ausführen auf | **Automatisch** (Grafikkarte, wenn sie hilft), **Grafikkarte** (am schnellsten, wenn das Modell passt), **Prozessor** (langsamer, lässt die Grafikkarte für Spiele frei) | Automatisch |
| Grafikspeicher für KI | Eine Obergrenze für den Grafikspeicher, den die KI nutzen darf | Durch die Voreinstellung festgelegt |
| Prozessorkerne | Weniger Kerne halten deinen PC reaktionsschnell | Durch die Voreinstellung festgelegt |
| Priorität | **Niedrig** (deine eigene Arbeit zuerst) oder **Normal** | Niedrig |
| Maximale Prozessornutzung | Eine Obergrenze für die Prozessornutzung | Kein Limit (100 %) |
| Zurückhalten, wenn | Die KI läuft mit niedrigster Priorität, nutzt höchstens die Hälfte deines Prozessors und kehrt danach zum Normalbetrieb zurück. Du kannst wählen: ein Spiel oder Programm im Vollbild läuft, der PC läuft im Akkubetrieb, eine Portal-App ist mit Live-Arbeit beschäftigt, etwa Diktieren | Vollbildprogramm, Akku und beschäftigte App sind an |

Unter **Speicher bei Inaktivität freigeben** in **Einstellungen > Allgemein** wählst du **Modell geladen lassen**, **Nach 5 Minuten** oder **Nach 10 Minuten** (Standard 10 Minuten, 5 im Akkubetrieb). Das Modell wird beim nächsten Mal neu geladen, wenn eine App fragt. Geladen lassen beantwortet Fragen schneller, belegt aber ständig Speicher.

Die Schieberegler-Hinweise zeigen, ob der gewählte Speicher ein Modell vollständig auf der Grafikkarte, teilweise auf dem Prozessor oder nur auf dem Prozessor unterbringt, und bieten ein kleineres Modell an, wenn das aktuelle nicht mehr passt.

[screenshot: Einstellungen > Leistung]

**Fehlerbehebung:** Langsame Antworten auf einem PC mit Grafikkarte bedeuten meist, dass das Modell nur teilweise auf die Grafikkarte passt. Wähle ein kleineres Modell oder erhöhe den Grafikspeicher für KI. Ruckelt dein PC bei der Nutzung, wähle Leise oder senke die maximale Prozessornutzung.

---

## 8. Benachrichtigungen

**Einstellungen > Benachrichtigungen** steuert kurze Windows-Meldungen, die erscheinen, wenn etwas deine Aufmerksamkeit braucht und das Fenster geschlossen ist. Sie bleiben still, solange ein Vollbildprogramm im Vordergrund ist, und wiederholen sich nie öfter als alle paar Minuten.

| Schalter | Bedeutung |
|---|---|
| Ein Modelldownload ist fertig oder fehlgeschlagen | Standardmäßig an |
| Eine App bittet um Erlaubnis | Standardmäßig an |
| Die KI-Engine stoppt oder eine Prüfung schlägt fehl | Standardmäßig an |
| Ein Gerät koppelt sich oder verbindet sich von unterwegs | Standardmäßig an (die Warnungen aus Abschnitt 10) |

Jeder lässt sich einzeln ausschalten. Mit **Auf Standard zurücksetzen** auf derselben Seite werden sie wiederhergestellt.

---

## 9. Andere Geräte diesen PC nutzen lassen

**Einstellungen > Andere Geräte.** Standardmäßig aus. Ist es an, können dein eigener Laptop oder ein anderer PC in deinem Heimnetzwerk fragen, ob sie die KI dieses PCs nutzen dürfen. Ein Gerät muss zuerst mit einem Code gekoppelt werden, und jede App darauf braucht trotzdem deine Erlaubnis.

### Gerät koppeln

1. Schalte **Anderen Geräten erlauben, die KI dieses PCs zu nutzen** ein. Windows fragt eventuell, ob eine Netzwerkregel erlaubt werden soll.
2. Drücke **Gerät koppeln**. Ein Code und ein QR-Code erscheinen, und **Link kopieren** kopiert einen Kopplungslink.
3. Öffne auf dem anderen Gerät seine Portal-App (oder Portal AI Host im Client-Modus, Abschnitt 12), wähle die Verbindung zu einem PC, und scanne dann den Code, gib ihn ein oder füge den Link ein.
4. Das Gerät prüft, ob es mit diesem PC spricht, bevor es etwas sendet, und zeigt dann den Namen des PCs und einen Fingerabdruck zur Bestätigung.

Der Code funktioniert einmal, 5 Minuten lang, für ein Gerät. Falsche Versuche verbrauchen ihn; erstelle mit **Gerät koppeln** einen neuen. **Kopplung abbrechen** beendet sie.

[screenshot: der Kopplungscode und der QR-Code]

### Gekoppelte Geräte

Jedes gekoppelte Gerät wird mit seiner letzten Nutzungszeit und einer Schaltfläche **Entfernen** aufgelistet. Ein Entfernen stoppt den Zugriff sofort, und laufende Anfragen enden. Um wieder zuzugreifen, muss es sich neu koppeln. Höchstens **16** Geräte können gekoppelt sein.

### Weitere Einstellungen in dieser Karte

- **Auch von unterwegs erreichen** (das Relais), siehe Abschnitt 13. Standard: an, sobald andere Geräte aktiviert sind.
- **Relais-Zugangsschlüssel**, siehe Abschnitt 13. Standard: aus.
- **Geräte dürfen Modelle ersetzen**, siehe Abschnitt 11. Standard: aus.

**Was über das Netzwerk läuft:** Anfragen und Antworten reisen verschlüsselt zwischen deinen eigenen Geräten. Die Verbindung im Heimnetzwerk nutzt ein Zertifikat, das das Gerät bei der Kopplung festgeheftet hat. Nichts geht an einen Server von uns, außer optional an das blinde Relais (Abschnitt 13).

**Fehlerbehebung:** *Ein Gerät findet diesen PC nicht:* Prüfe, ob beide im selben Netzwerk sind, dass dieser PC wach ist und der Host läuft. *Der Code ist „nicht richtig“ oder „abgelaufen“:* Erstelle einen neuen. *„Nicht im Heimnetzwerk“:* Koppele zuerst einmal zu Hause.

---

## 10. Geräte an diesem PC

Die Seite **Geräte dieses PCs** zeigt eine Karte pro gekoppeltem Laptop. Hier siehst du, was jeder Laptop gerade tut, und kannst ihn begrenzen.

[screenshot: Geräte an diesem PC mit zwei Gerätekarten]

### Was eine Karte zeigt

- Status (Online, Inaktiv, Offline), Route (Heimnetzwerk oder Relais (unterwegs)) und zuletzt gesehen.
- Was gerade läuft und was wartet.
- Heutige Anfragen und Tokens mit einer 7-Tage-Leiste, pro App.
- Das genutzte Modell, wer es geladen hat, sowie Geschwindigkeit in Tokens pro Sekunde und Zeit bis zum ersten Byte.

### Bedienelemente

| Bedienelement | Was es bewirkt |
|---|---|
| Pausieren / Fortsetzen | Pausiert das ganze Gerät oder eine seiner Apps. Der Laptop erhält eine klare Antwort „pausiert“ und nutzt seine eigene Rückfalloption, falls du eine eingerichtet hast. |
| Umbenennen | Ändert den angezeigten Namen des Geräts |
| Grenzen | Priorität, Tageslimits, Ruhezeiten und erlaubte Modelle (unten) |
| Modellsteuerung (Schalter) | Erlaubt diesem Gerät, Modelle zu laden und zu entladen. Siehe Abschnitt 11 |
| Zurücknehmen | Entlädt das Modell, das dieses Gerät geladen hat. Die eigene Wahl dieses PCs kehrt zurück. Keine Modelldatei wird berührt. |
| Vergessen | Entfernt das Gerät. Sein Zugriff endet sofort und sein Nutzungsverlauf wird gelöscht. Modelle bleiben unberührt. |

**Grenzen** (unter der Schaltfläche **Grenzen** der Karte, dann **Speichern**):

- **Gleiche Priorität wie die Apps dieses PCs.** Aus (Standard): Die eigenen Apps dieses PCs kommen zuerst. An: Das Gerät wartet in derselben Schlange.
- **Anfragen pro Tag** und **Tokens pro Tag.** Standard: kein Limit. Ist das Kontingent aufgebraucht, wird dem Gerät gesagt, dass es nach Mitternacht (Uhrzeit dieses PCs) wiederkommen soll.
- **Ruhezeiten** mit **Von** und **Bis** (Uhrzeit dieses PCs). Das Gerät erhält in diesen Zeiten keine Antworten. Standard: aus.
- **Modelle, die dieses Gerät nutzen darf.** Standard: alle Modelle.

### Spielmodus

Oben auf der Seite. **Standard: aus.** Ist er an, werden Anfragen anderer Geräte als „beschäftigt“ beantwortet, während du spielst, damit Spiele die Grafikkarte behalten. Dieser PC gilt als „im Spiel“, wenn Windows ein Spiel im Vollbild meldet oder wenn ein anderes Programm die 3D-Engine der Grafikkarte etwa 10 Sekunden lang zu 40 % oder mehr auslastet. Spiele im Fenster oder im randlosen Fenster zählen deshalb ebenfalls. Es endet etwa 30 Sekunden nachdem die Last aufhört. Sind die Grafikzähler auf deinem PC nicht verfügbar, wird nur die Vollbildprüfung genutzt. Der Laptop greift dann auf seine eigene Einstellung zurück.

### Warnungen

Eine Windows-Benachrichtigung erscheint, wenn sich ein neues Gerät koppelt und wenn sich ein Gerät von unterwegs über das Relais verbindet. Schalte sie unter **Einstellungen > Benachrichtigungen > Ein Gerät koppelt sich oder verbindet sich von unterwegs** aus.

### Daten

Die Nutzungszahlen sind nur Zählwerte (Anfragen und Tokens pro App und Tag, 7 Tage gespeichert). Kein Text von Eingaben oder Antworten wird gespeichert. **Vergessen** löscht nur die Nutzungsdatei dieses Geräts und sonst nichts.

---

## 11. Modellsteuerung und zwei Modelle gleichzeitig

Standardmäßig nutzt ein gekoppelter Laptop das Modell, das dieser PC gerade geladen hat. Du kannst einem Gerät mehr geben.

### Modellsteuerung

**Einstellungen > Andere Geräte**, in der Zeile des Geräts: **Modellsteuerung** (Standard: aus). Ist sie an, kann der Laptop:
- installierte Modelle auf diesem PC laden und entladen,
- wählen, wo ein Modell läuft (Automatisch, Grafikkarte, Systemspeicher oder Nur Prozessor),
- wählen, welche Modelle dieses PCs jede seiner eigenen Apps nutzt.

Er kann Modelle nie herunterladen, entfernen, prüfen oder importieren, Einstellungen ändern oder deine Dateien anfassen, mit oder ohne Freigabe. Schaltest du es aus, wird der nächste Aufruf des Geräts abgelehnt. Das Entfernen des Geräts entfernt auch die Freigabe, und erneutes Koppeln stellt sie nicht wieder her.

Der PC zeigt, wer was geladen hat: „Geladen von <Gerät>: <Modell> (Grafikkarte)“. **Zurücknehmen** (auf der Karte und in den Einstellungen) entlädt das Modell, das ein Gerät geladen hat. Die eigene Wahl dieses PCs kehrt zurück und wird bei der nächsten Anfrage geladen.

### Zwei Modelle gleichzeitig

Mit Modellsteuerung darf ein Laptop ein zweites Modell auf diesem PC geladen halten, zum Beispiel eins auf der Grafikkarte und eins im Systemspeicher.
- Dein eigenes Modell bleibt im ersten Platz. Das Modell eines Geräts kommt auf den zweiten Platz, sodass ein Gerät dein Modell nie verdrängt, nur um sein eigenes zu bekommen.
- Jeder Platz hat seine eigene Warteschlange, sodass zwei Modelle gleichzeitig antworten können. Eine Anfrage, die das zweite Modell nennt, geht dorthin; alles andere geht an dein eigenes.
- Vor dem Laden zählt der Host, was der andere Platz bereits nutzt, gegen deine Grenzen. Ein zweites Modell, das nicht passt, wird abgelehnt („nicht genug Speicher“). Nichts wird stillschweigend gestoppt.
- Deine eigenen Entscheidungen haben immer Vorrang. Wählst du ein anderes Modell, entlädst du eins, oder dein eigenes Modell braucht den Speicher, wird das Modell des Geräts gestoppt und das Gerät wird informiert.
- Das zweite Modell eines Geräts wird nie dauerhaft geladen gehalten; es entlädt sich wie jedes andere, wenn es untätig ist.
- **Nur Prozessor** führt ein Modell ohne Grafikschichten aus und nutzt etwa die Hälfte des Prozessors, zum Beispiel ein Hilfsmodell auf dem Prozessor, während ein anderes die Grafikkarte nutzt.

[screenshot: Seite „Modelle“ mit zwei Modellen, die von verschiedenen Besitzern geladen wurden]

### Geräte dürfen Modelle ersetzen

**Einstellungen > Andere Geräte > Geräte dürfen Modelle ersetzen** (Standard: aus). Aus: Ein Gerät kann kein Modell entladen, das deine eigenen Apps nutzen. An: Es kann es, und deine Apps laden ihr Modell bei ihrer nächsten Anfrage erneut. Bei zwei Geräten behält das zuerst geladene sein Modell.

---

## 12. Den Heim-PC vom Laptop aus nutzen (Client-Modus)

Der Client-Modus lässt Portal AI Host auf einem Laptop die KI eines anderen PCs von dir nutzen, statt selbst ein Modell auszuführen. Apps auf dem Laptop funktionieren wie gewohnt.

### Einrichtung

1. Schalte auf dem Heim-PC **Anderen Geräten erlauben, die KI dieses PCs zu nutzen** ein und drücke **Gerät koppeln** (Abschnitt 9). Drücke **Link kopieren** und schicke den Link an den Laptop.
2. Öffne auf dem Laptop **Einstellungen > Heim-PC** (oder wähle im Willkommensfenster **Heim-PC nutzen**).
3. Wähle unter **Wo die KI läuft** die Option **Heim-PC nutzen**. Es öffnet sich ein Fenster **Heim-PC nutzen**. Füge den Link mit **Link einfügen** ein, drücke **Weiter**, prüfe den PC-Namen und den Fingerabdruck und dann **Koppeln**. Benenne den Laptop.
4. Die Startseite zeigt jetzt den Namen des PCs und einen Status: **Verbunden - Heimnetzwerk**, **Verbunden - über Relais**, **Verbindung wird hergestellt...** oder **Nicht erreichbar**.

[screenshot: das Kopplungsfenster auf dem Laptop]

### Einstellungen (Einstellungen > Heim-PC)

| Einstellung | Auswahl | Standard |
|---|---|---|
| Wo die KI läuft | Dieser PC / Heim-PC nutzen | Dieser PC |
| Wenn der Heim-PC nicht erreichbar ist | **Keine** (Apps nutzen ihre eingebaute KI) / **Modell dieses Laptops nutzen** (lädt bei Bedarf) | Keine |
| Verbindung unterwegs | **Automatisch** / **Manuell** (dann gilt ein Schalter **Relais nutzen** nur für diese Sitzung und wird beim Schließen der App zurückgesetzt) | Automatisch |

- Wenn der Heim-PC das erste Mal nicht erreichbar ist, wirst du einmal gefragt, was passieren soll. Du kannst es hier jederzeit ändern.
- **PC koppeln** koppelt erneut; **Entkoppeln** vergisst den PC (Modelle und Downloads bleiben, wie sie sind); **PC wechseln** (auf der Startseite) öffnet diese Einstellungen; **Hier KI ausführen** (auf der Startseite) beendet den Client-Modus und behält die gespeicherte Kopplung.
- Mit Modellsteuerung, die der Heim-PC erlaubt, erscheint **Modelle auf <PC-Name>** mit Schaltflächen zum Laden, Entladen und Platzieren, plus **PC-Modell pro App**. Ohne Erlaubnis steht auf der Seite: „Bitte den Besitzer von <PC-Name> bitten, die Modellsteuerung für diesen Laptop einzuschalten“.
- Ein Laptop im Client-Modus akzeptiert keine Verbindungen anderer Geräte und kann nicht als Zwischenstation zu deinem Heim-PC dienen.

### Was mit den eigenen Modellen dieses Laptops passiert

Beim Wechsel in den Client-Modus wird das geladene Modell des Laptops entladen, um Speicher freizugeben. Modelldateien, Downloads und der Modellordner bleiben unberührt. Beim Verlassen des Client-Modus wird das Modell bei Bedarf wieder geladen.

### Fehlerbehebung

- **Nicht erreichbar:** Prüfe, ob der Heim-PC eingeschaltet ist und im selben Netzwerk hängt, oder schalte das Relais ein.
- **„Dieser Laptop wurde vom PC entfernt“:** Der Heim-PC hat ihn entfernt. Wähle **Erneut koppeln**.
- **„Die Identität von <PC> hat sich geändert“:** Das Zertifikat des PCs passt nicht mehr zu dem, was der Laptop gespeichert hat. Wenn du das nicht erwartet hast, koppele nicht erneut. Andernfalls entkoppeln und dann neu koppeln.
- **Kopplung von unterwegs schlägt fehl:** Koppele zuerst einmal zu Hause; der Kopplungslink muss auf eine Adresse in deinem Heimnetzwerk zeigen.
- **Pausiert, Ruhezeiten, ein laufendes Spiel oder ein aufgebrauchtes Tageslimit:** Der PC sagt dem Laptop den Grund; der Laptop nutzt seine Rückfalloption.

**Wo es gespeichert wird:** Der Laptop bewahrt seine Kopplung im Datenordner des Hosts unter `client\` auf. Der Geräteschlüssel und die Tokens sind für deinen Windows-Benutzer geschützt. **Entkoppeln** entfernt sie. Sicherungen dieses Ordners lassen sich unter einem anderen Windows-Benutzer nicht wiederherstellen.

---

## 13. Unterwegs: das Relais und der Relais-Zugangsschlüssel

Das Relais ermöglicht einem gekoppelten Gerät, von überall auf deinen PC zuzugreifen. **Einstellungen > Andere Geräte > Auch von unterwegs auf diesen PC zugreifen.**

- Dein PC verbindet sich nach außen mit einem kleinen Relais-Dienst. Das Relais leitet nur verschlüsselte Bytes weiter, die es nicht lesen kann, und speichert keine Inhalte. Jedes Gerät muss trotzdem gekoppelt sein und jede Anfrage signieren.
- Die Statuszeile zeigt **Von unterwegs erreichbar**, **Nur Heimnetzwerk**, **Verbindung zum Relais wird hergestellt...** oder **Relais nicht erreichbar**.
- Es läuft über den üblichen Webport, sodass die meisten Büronetzwerke es erlauben.
- **Firmengeräte:** Arbeitsinhalte landen auf deinem privaten PC. Prüfe zuerst die Regeln deines Arbeitgebers.

### Relais-Zugangsschlüssel

**Einstellungen > Andere Geräte > Relais-Zugangsschlüssel** (Standard: aus). Ist er an, können nur Geräte, die mit dem Schlüssel im Kopplungscode gekoppelt sind, über das Relais auf diesen PC zugreifen. So verbrauchen Fremde nicht die Relais-Plätze deines PCs.

- Schalte ihn **vor dem Koppeln** ein. Zuvor gekoppelte Geräte müssen sich für das Relais neu koppeln (das Heimnetzwerk funktioniert weiter).
- **Neuer Schlüssel** erstellt nach einer Bestätigung einen frischen Schlüssel. Jedes gekoppelte Gerät muss sich für das Relais neu koppeln; zu Hause ändert sich nichts. Das Entfernen eines Geräts ändert den Schlüssel nicht, nutze also **Neuer Schlüssel**, nachdem du ein Gerät entfernt hast, dem du nicht traust.
- Ist die Schlüsseldatei nicht verwendbar, bleibt das Relais aus, statt offen zu sein, und das Heimnetzwerk funktioniert weiter. Eine beschädigte Schlüsseldatei wird unter einem Namen mit `.bad` beiseitegelegt und nie überschrieben.
- Bleibt der Schalter nicht an, muss der Relais-Dienst auf seiner Seite eventuell aktualisiert werden. Schalte ihn dann wieder aus.

**Grenzen:** Das Relais begrenzt, wie schnell und wie viel ein Gerät senden kann, damit ein einzelnes Gerät den Dienst nicht überflutet. Normale Nutzung liegt weit darunter.

---

## 14. Datenschutz und Absturzberichte

- **Keine Analysedaten oder Nutzungsverfolgung.** Nur ein Absturzbericht kann deinen PC verlassen, und nur, wenn du ihn eingeschaltet lässt.
- **Dein Text bleibt auf deinen Geräten.** Eingaben und Antworten werden vom Host nie gespeichert oder protokolliert. Protokolle enthalten nur Zählwerte, Zeiten und Zustände.
- **Absturzberichte sind standardmäßig an, und du kannst sie ausschalten.** Stürzt das Fenster oder der Hintergrunddienst ab, hilft ein kurzer Bericht uns, den Fehler zu beheben. Du kannst es jederzeit unter **Einstellungen > Datenschutz > Absturzberichte senden** ausschalten; es wirkt sofort. Das Willkommensfenster bietet dieselbe Wahl, standardmäßig angekreuzt, bevor etwas gesendet wird.
  - **Gesendet:** der Fehlertyp und wo er auftrat (Funktions- und Modulnamen), die Versionen von App und Windows, ob es ein Store-Build ist und ob der Fehler aus dem Fenster oder dem Host kam.
  - **Nie gesendet:** Eingaben oder Antworten, alles, was du tippst, Dateinamen oder Ordnerpfade, dein Computer- oder Benutzername, welche Apps den Host nutzen, oder deine IP-Adresse. Fehlertext wird verworfen.
  - **90 Tage gespeichert**, danach gelöscht.
  - **Was gesendet wird** (Einstellungen > Datenschutz) öffnet die Datenschutzseite.
- **Alles ist verschlüsselt**, wenn es deinen PC zu einem anderen deiner Geräte verlässt.
- **Datenschutzerklärung:** Info > **Datenschutzerklärung**.
- Fragen oder eine Bitte, einen Bericht zu löschen: mindthaapp@gmail.com.

[screenshot: Einstellungen > Datenschutz mit dem Schalter für Absturzberichte]

---

## 15. Wo deine Daten gespeichert sind, Sicherung und Entfernung

Alles liegt im eigenen Datenordner der App. **Info > Datenordner öffnen** öffnet ihn.

| Was | Wo (im Datenordner) | Sichern? |
|---|---|---|
| Heruntergeladene Modelle | `models\` oder `Portal AI Host Models\` im Ordner, den du in den Einstellungen gewählt hast | Optional. Sie lassen sich erneut herunterladen. Sie sind groß. |
| Einstellungen | `settings.json` | Ja |
| Erlaubte Apps und ihre Tokens | Der App-Speicher des Hosts | Nicht übertragbar (für deinen Windows-Benutzer geschützt) |
| Gekoppelte Geräte, Grenzen, Nutzungsstatistiken | Der Gerätespeicher des Hosts und eine kleine Statistikdatei pro Gerät | Nicht übertragbar |
| Laptop-Kopplung (Client-Modus) | `client\` | Nicht übertragbar. Auf einem neuen PC neu koppeln. |
| Protokolle | `logs\` (rollierend, auf 10 MB begrenzt) | Nein |
| Erkennungsdatei für Apps | `discovery\host.json` | Nein |

**Sichern:** Schließe Portal AI Host und kopiere dann den Datenordner. Einstellungen und Modelle lassen sich durch Zurückkopieren wiederherstellen. App-Berechtigungen, Gerätekopplungen und Tokens sind für deinen Windows-Benutzer und deinen PC geschützt. Auf einem neuen PC erlaubst du Apps und koppelst Geräte deshalb erneut.

**Entfernen:**
- *Ein Modell:* **Modelle > Entfernen**. Gelöscht werden nur Dateien, die der Host heruntergeladen hat.
- *Überbleibsel:* **Modelle > Speicherplatz freigeben**.
- *Ein Gerät:* **Vergessen** (Geräte an diesem PC) oder **Entfernen** (Einstellungen).
- *Der Zugriff einer App:* **Zugriff entfernen** (Geräte und Apps).
- *Alles:* Deinstalliere die App. Ein selbst gewählter Modellordner bleibt erhalten; lösche seinen Ordner `Portal AI Host Models` selbst, wenn du ihn loswerden willst.

Die App löscht nie etwas, das sie nicht selbst angelegt hat, und überschreibt nie deine Dateien. Ein Modell, das du selbst hinzugefügt hast, wird nur als Kopie des Hosts entfernt (deine Originaldatei bleibt unberührt).

---

## 16. Tastenkombinationen

| Tasten | Aktion |
|---|---|
| Strg+1 | Start |
| Strg+2 | Modelle |
| Strg+3 | Geräte und Apps |
| Strg+4 | Ausprobieren |
| Strg+, | Einstellungen |
| F1 | Info |
| Strg+Eingabe | Fragen, auf Ausprobieren |
| Esc | Neuigkeiten oder einen Dialog schließen |

Die Liste steht auch unter dem App-Menü > **Tastenkombinationen**.

Darstellung: **Einstellungen > Darstellung** bietet Design (Standard: deine Windows-Einstellung), Fenstermaterial (Mica), Ecken (Abgerundet), Dichte (Standard), Textgröße (Normal) und Bewegung (Windows folgen). Der Windows-Hochkontrastmodus verwendet immer einfarbige Systemfarben.

---

## 17. Fehlerbehebung und FAQ

**Der Host meldet, dass er nicht läuft.** Drücke auf der Startseite **Portal AI Host starten**. Lässt er sich nicht starten, starte den PC neu; schlägt auch das fehl, installiere aus dem Store neu.

**„Ein anderer Windows-Benutzer auf diesem PC führt Portal AI Host bereits aus.“** Bitte diesen Benutzer, es zu beenden, oder melde dich als dieser Benutzer an.

**Antworten sind langsam.** Siehe Abschnitt 7. Ein Modell, das für deinen Grafikspeicher zu groß ist, läuft teilweise auf dem Prozessor.

**Eine App meldet, dass keine KI verfügbar ist.** Prüfe, ob die App erlaubt ist (Geräte und Apps), nicht pausiert ist und ob ein Modell installiert ist, das nicht zu groß für den PC ist.

**Die erste Antwort ist langsam, die späteren sind schnell.** Das Modell hat geruht. Es lädt bei der ersten Anfrage. Wähle **Modell geladen lassen** in Einstellungen > Allgemein, um das zu vermeiden.

**Werden meine Daten irgendwohin gesendet?** Nein. Eingaben und Antworten bleiben auf deinen Geräten. Nur Absturzberichte können den PC verlassen (Abschnitt 14), und du kannst sie ausschalten.

**Kann ich es ohne Internet nutzen?** Ja, sobald ein Modell heruntergeladen ist.

**Kostet es etwas?** Nein.

**Welches Modell soll ich wählen?** Das empfohlene. Wähle ein kleineres, wenn sich dein PC träge anfühlt, ein größeres, wenn du bessere Antworten willst und den Speicher hast.

**Der Download hängt bei „Download wird geprüft“.** Warte. Große Dateien brauchen eine Minute zur Prüfung.

**Die Kopplung sagt „zu viele Geräte“.** Die Grenze liegt bei 16 Geräten. Entferne eines.

**Wo ist das Symbol im Infobereich?** Klicke auf den Pfeil neben der Uhr, um ausgeblendete Symbole anzuzeigen.

Wenn du immer noch nicht weiterkommst, nutze **Info > Version kopieren**, schreibe dann an mindthaapp@gmail.com und füge die Version in deine Nachricht ein.

---

## 18. Kommt später

Das ist geplant und noch nicht in der App.

- **Modellkategorien auf der Seite Modelle:** gruppiert nach Aufgabe (Chat und Schreiben, Programmieren, logisches Denken, Bildverstehen, Embeddings, Sprache) mit Filtern und Suche.
- **Bild- und Videoerzeugung** auf diesem PC, einschließlich eines „Regisseurs“, der Szenen zu einem Video verkettet. Videomodelle erscheinen als „Demnächst“, bis die Engine existiert.
- **Wahl, welches Modell antwortet**, sowie Modelle, die einander um Hilfe bitten, in AI Portal.
- **macOS-Version.**

---

## 19. Support

- **Rückmeldungen und Ideen:** Info > **Rückmeldung senden oder eine Idee vorschlagen**.
- **Support- und Datenschutzfragen:** mindthaapp@gmail.com.
- **Version:** Info zeigt sie, und **Version kopieren** legt sie für deine Nachricht in die Zwischenablage.
- **Lizenzen:** Portal AI Host enthält llama.cpp (MIT-Lizenz), das die Modelle auf deinem PC ausführt. Modelle werden unter ihren eigenen Lizenzen (Apache 2.0 oder MIT) heruntergeladen. Siehe Info.
