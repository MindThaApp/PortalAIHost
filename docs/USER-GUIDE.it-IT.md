# Portal AI Host: guida utente

Portal AI Host esegue un modello di IA sul tuo PC e lo condivide con le tue app Portal. Le tue domande e i tuoi file restano sui tuoi dispositivi. Questa guida spiega ogni parte dell'app, cosa fanno le sue impostazioni e cosa fare quando qualcosa non va.

I punti in cui andrebbe un'immagine sono indicati con `[screenshot: ...]`.

## Indice

1. Avvio rapido
2. Requisiti di sistema, installazione, aggiornamento, disinstallazione
3. Orientarsi nell'app
4. Modelli
5. App e autorizzazioni
6. Provalo
7. Impostazioni di prestazione
8. Notifiche
9. Consentire ad altri dispositivi di usare questo PC
10. Dispositivi su questo PC
11. Controllo dei modelli e due modelli insieme
12. Usare il PC di casa dal portatile (modalità client)
13. Fuori casa: il relay e la chiave di accesso al relay
14. Privacy e rapporti di arresto anomalo
15. Dove sono archiviati i dati, backup e rimozione
16. Scorciatoie da tastiera
17. Risoluzione dei problemi e FAQ
18. In arrivo
19. Supporto

---

## 1. Avvio rapido

1. Installa Portal AI Host dal Microsoft Store e aprilo.
2. La finestra di benvenuto controlla il tuo PC (memoria, scheda grafica, spazio libero su disco) e consiglia un modello. Scegli **Avvia con le impostazioni consigliate**, oppure **Scarica** per scegliere tu il modello. **Scegli più tardi** salta questo passaggio.
3. Cerca l'icona vicino all'orologio. Portal AI Host attende lì in silenzio. Fai clic su di essa per vedere rapidamente lo stato.
4. Apri **Provalo** e fai una domanda breve per verificare che il modello funzioni.
5. La prima volta che un'app Portal vuole usare l'IA, un avviso ti chiede di consentire o negare.

[screenshot: la finestra di benvenuto con il modello consigliato]

Se invece vuoi che un portatile usi l'IA del tuo PC di casa, scegli **Usa il mio PC di casa** nella finestra di benvenuto. Vedi la sezione 12.

**Gratuito o a pagamento:** Portal AI Host è gratuito. Nessuna funzione descritta in questa guida richiede un account, un accesso o un acquisto.

---

## 2. Requisiti di sistema, installazione, aggiornamento, disinstallazione

### Requisiti

| | Minimo | Consigliato |
|---|---|---|
| Windows | Windows 11 (21H2, build 22000) o successiva, x64 o Arm64 | Lo stesso |
| Memoria | 8 GB | 16 GB o più (32 GB o più per i modelli grandi) |
| Grafica | Nessuna (il modello gira allora sul processore, più lento) | Una scheda grafica compatibile con Vulkan con 8 GB di memoria video o più (AMD, NVIDIA o Intel) |
| Spazio libero su disco | 5 GB | 25 GB |

I modelli occupano da 2 a 20 GB ciascuno. I PC Arm64 eseguono il modello sul processore. La connessione Internet serve solo per scaricare i modelli e per il relay; una volta che un modello è sul PC, funziona offline.

### Installazione

Installa dal Microsoft Store. Non serve altro: niente Python, niente riga di comando, nessun altro software. Il motore di IA fa parte dell'app.

### Avvio con Windows

**Impostazioni > Generali > Avvia con Windows** (predefinito: disattivato, tranne se nella finestra di benvenuto scegli «Avvia con le impostazioni consigliate», che lo attiva). Quando è attivo, Portal AI Host attende nell'area di notifica dopo l'accesso, così le app possono usarlo subito.
- Se l'interruttore dice che è disponibile solo nell'app installata, stai eseguendo una copia di sviluppo.
- Se lo hai disattivato in Gestione attività (app di avvio), riattivalo lì.
- Se la tua organizzazione lo imposta, l'interruttore è bloccato.

### Aggiornamento

Lo Store aggiorna l'app. Dopo un aggiornamento, **Novità** si apre una volta. Puoi riaprirlo dal menu dell'app. I modelli restano dove sono tra un aggiornamento e l'altro.

### Disinstallazione

Rimuovi l'app in **Impostazioni di Windows > App**. I modelli nella cartella predefinita sono archiviati nei dati dell'app e vengono rimossi con essa. Una cartella di modelli personalizzata che hai scelto non viene toccata. Consulta la sezione 15 prima di disinstallare se vuoi conservare qualcosa.

---

## 3. Orientarsi nell'app

Il lato sinistro ha cinque pagine.

| Pagina | A cosa serve | Scorciatoia |
|---|---|---|
| Home | Stato, modello attivo, uso della memoria, l'app che sta usando l'IA in questo momento | Ctrl+1 |
| Modelli | Scaricare, usare e rimuovere modelli | Ctrl+2 |
| Dispositivi e app | Consentire o rimuovere app Portal, sospenderle, impostare la priorità | Ctrl+3 |
| Provalo | Fare al modello una breve domanda | Ctrl+4 |
| Impostazioni | Tutto ciò che è configurabile | Ctrl+, |

Una sesta pagina, **Dispositivi di questo PC**, elenca i portatili e i telefoni abbinati a questo PC (vedi sezione 10). Il menu dell'app contiene Impostazioni, Scorciatoie da tastiera, Novità e Informazioni.

**Home** indica se l'IA è **Pronta**, **Occupata a rispondere**, **In preparazione del modello**, **In pausa** (il modello si riattiva quando un'app lo chiede) oppure non ha **ancora un modello**. **ADESSO** mostra quale app sta ricevendo una risposta e circa quante parole al secondo. Mostra anche quanta memoria e memoria grafica usa il modello, e un pulsante **Carica ora** / **Scarica ora**. **OGGI** conta le richieste di ogni app. Una scheda di stato mostra «Tutto a posto» oppure elenca ciò che richiede attenzione, ciascuno con una correzione in un clic come Carica il modello, Riavvia il motore, Controlla i file o Apri le impostazioni di prestazione.

[screenshot: la pagina Home mentre un'app usa il modello]

L'icona nell'area di notifica apre una piccola finestra: modello, memoria, ultima richiesta e coda, con i pulsanti **Apri app** e **Scarica modello**.

[screenshot: la finestra dell'area di notifica]

Se il programma host non è in esecuzione, Home offre **Avvia Portal AI Host**. Se si è chiuso in modo imprevisto più di una volta, una barra **Modalità provvisoria** indica che è stato avviato con impostazioni prudenti. Le tue app continuano a funzionare; riavvia il PC se succede ancora.

---

## 4. Modelli

Un modello è l'IA che fa il lavoro. **Modelli** (Ctrl+2) elenca ciò che hai e ciò che puoi aggiungere.

### Modello consigliato

Il primo avvio e la pagina Home consigliano «il modello migliore per questo PC», con un motivo chiaro, in base alla memoria, alla memoria grafica e allo spazio libero su disco. Quando ne diventa disponibile uno migliore, una barra indica **È disponibile un modello migliore**. Il modello attuale resta finché quello nuovo non è pronto, e decidi tu quando rimuovere quello vecchio.

### Scaricare un modello

1. Apri **Modelli**. Scegli un modello in **Modelli che puoi aggiungere**.
2. Premi **Scarica**. Una barra mostra l'avanzamento e il tempo rimanente.
3. **Pausa** e **Riprendi** sono sempre disponibili. **Interrompi** scarta la parte già scaricata (dopo conferma).
4. L'host controlla i file scaricati prima di usarli.
5. Quando è completato, premi **Usa questo modello**.

I download possono essere ripresi dopo un riavvio e vengono verificati file per file. Un download richiede spazio libero sufficiente; in caso contrario la pagina indica quanto manca e offre **Cambia cartella nelle Impostazioni**. Il PC resta sveglio mentre un download è in corso (Impostazioni > Prestazioni, attivo per impostazione predefinita).

[screenshot: la pagina Modelli con un download in corso]

Un modello contrassegnato come «Questo modello potrebbe essere troppo grande per questo PC» può comunque essere scaricato, ma potrebbe funzionare lentamente o non entrare.

### Dettagli, controllo dei file, nuovo download, rimozione

- **Dettagli** mostra a cosa serve il modello, cosa tenere presente, la memoria che richiede, la sua licenza, quando è stato usato l'ultima volta e quanto spazio occupa.
- **Controlla i file** verifica di nuovo un modello sul disco. Non elimina mai nulla. Un modello danneggiato viene contrassegnato come **Danneggiato**.
- **Scarica di nuovo** (per un modello danneggiato) rimuove la copia danneggiata e la scarica di nuovo. Non viene eliminato nient'altro.
- **Rimuovi** toglie un modello da questo PC dopo una conferma che indica quanto spazio verrà liberato. Se il modello è in uso, l'host lo scarica prima e chiede cosa usare dopo. Vengono eliminati solo i file scaricati dall'host.

### Aggiungere un tuo modello

**Aggiungi un tuo modello...** ti permette di scegliere un file `.gguf`. L'host crea una propria copia nella cartella dei modelli. L'originale non viene mai modificato né spostato. La riga è contrassegnata come **Aggiunto da te**, e l'host non può stimare quanta memoria serve, quindi provalo e vedi. **Rimuovi** su un modello del genere elimina solo la copia dell'host.

### Liberare spazio

**Libera spazio** elenca i residui creati dall'app stessa: download incompleti che non si possono riprendere, vecchie copie rimaste dopo lo spostamento dei modelli e modelli che un controllo ha trovato danneggiati. Confermi prima che venga rimosso qualcosa. I modelli che usi e i file che hai aggiunto tu non vengono mai elencati. I download incompleti che puoi ancora riprendere vengono conservati per una settimana.

### Cartella dei modelli e spostamento dei modelli

**Impostazioni > Cartella dei modelli** stabilisce dove vanno i nuovi download. Lascia vuoto per la cartella predefinita, oppure digita un percorso completo come `D:\Modelli` e premi Invio. I modelli già scaricati non vengono spostati né eliminati: restano dove sono e continuano a comparire nell'elenco. Per spostarli, accetta **Sposta lì i miei modelli**. Vengono copiati, controllati e solo dopo vengono rimosse le vecchie copie. Il modello in uso viene scaricato durante l'operazione, e **Annulla spostamento** lascia tutto dov'era.

### Budget di memoria e barre

Le barre **Budget di memoria** nella pagina Modelli mostrano RAM e memoria grafica: quanto usano i modelli caricati, quanto viene riservato mentre un modello si carica e quanto resta libero rispetto ai tuoi limiti (vedi sezione 7). Un caricamento che supererebbe il tuo limite di memoria viene rifiutato con un motivo chiaro prima che venga fermato qualcosa.

[screenshot: le barre del Budget di memoria con due modelli caricati]

### Posizionamento: scheda grafica, memoria o solo processore

Dove viene eseguito un modello è deciso da **Impostazioni > Prestazioni > Esegui su** (Automatico, Scheda grafica, Processore). Un modello che non entra nella scheda grafica viene eseguito in parte sul processore (più lentamente). Vedi anche il posizionamento per modello nella sezione 11, che include **Solo processore**.

### Limiti e gratuito o a pagamento

- Tutti i modelli dell'elenco sono gratuiti per uso commerciale (licenze Apache 2.0 o MIT); ogni modello mostra la propria licenza.
- I modelli vengono scaricati dalle pagine dei loro editori su Hugging Face. L'host accetta solo un elenco di modelli attendibile e firmato. Se non riesce a caricarlo, una barra indica **Elenco modelli non disponibile**; i modelli già presenti su questo PC continuano a funzionare.

### Risoluzione dei problemi

- *Nessun modello elencato per il download:* controlla la connessione Internet e premi **Riprova** in Home.
- *Il download si interrompe:* premi **Riprendi**; se lo spazio libero è poco, libera spazio o cambia cartella.
- *Modello «potrebbe essere troppo grande»:* scegline uno più piccolo, oppure riduci il carico in Impostazioni > Prestazioni.

---

## 5. App e autorizzazioni

Apri **Dispositivi e app** (Ctrl+3).

- **In attesa della tua risposta:** un'app che ha chiesto di usare l'IA compare qui e come avviso («Consentire a Voice Portal di usare l'IA su questo PC?»). Scegli **Consenti**, **Non consentire** o **Decidi più tardi**. L'avviso indica se l'app è un'app Portal verificata, un'app installata che non è un'app Portal, oppure un'app non verificata (in tal caso indica da dove viene eseguita; consentila solo se l'hai avviata tu).
- **App che possono usare il tuo host IA:** ogni app consentita mostra le richieste di oggi e quando è stata usata l'ultima volta.
  - **Sospendi** rifiuta le nuove richieste finché non riprendi; l'app mantiene l'accesso.
  - **Priorità** (Alta, Normale, Bassa) decide chi passa per primo quando più app chiedono contemporaneamente. Le richieste interattive passano prima del lavoro in batch, e una richiesta che attende a lungo sale di posizione, così un'app a bassa priorità non resta mai senza risposta.
  - **Rimuovi accesso** ferma l'app finché non la consenti di nuovo. **Annulla** compare per un momento.

Le app devono chiedere prima di poter usare l'host. L'host ascolta solo su questo PC, a meno che tu non attivi la condivisione con altri dispositivi (sezione 9).

[screenshot: la pagina Dispositivi e app con un'app in attesa e un'app consentita]

Per gli sviluppatori: l'host offre un'API locale compatibile con OpenAI su `http://127.0.0.1:41870` (chat completions con streaming, embeddings, elenco dei modelli). Vedi `api-v1.md`.

**Dove viene archiviato:** le app consentite e i loro token sono conservati nella cartella dati dell'host, protetti per il tuo utente Windows. Rimuovere l'accesso di un'app elimina il suo token.

---

## 6. Provalo

**Provalo** (Ctrl+4) verifica che il modello funzioni. Digita una domanda breve e premi **Chiedi** (o Ctrl+Invio). **Interrompi** termina la risposta, **Cancella** svuota la pagina. Vengono proposte domande suggerite. Mostra quanto tempo hanno impiegato le prime parole e la velocità in token al secondo. Nulla di ciò che digiti qui viene salvato.

[screenshot: Provalo con una risposta e la riga dei tempi]

---

## 7. Impostazioni di prestazione

**Impostazioni > Prestazioni** evita che l'IA rallenti il tuo PC. Le modifiche si applicano subito; un modello che non entra più viene ricaricato con i nuovi limiti oppure scaricato (resta sul disco). **Ripristina i valori predefiniti** riporta la sezione allo stato iniziale.

| Impostazione | Cosa fa | Predefinito |
|---|---|---|
| Prestazioni | Un preset: **Silenzioso** (circa metà velocità, il PC resta reattivo), **Bilanciato** (buona velocità, i tuoi programmi hanno la precedenza), **Veloce** (risposte più rapide, altri programmi possono rallentare), **Personalizzato** | Bilanciato |
| Memoria massima per l'IA | L'IA non usa mai più memoria di così | 80% della memoria del PC |
| Lascia sempre libero | Memoria che resta libera per i tuoi altri programmi | Impostato dal preset |
| Esegui su | **Automatico** (scheda grafica quando aiuta), **Scheda grafica** (il più veloce, se il modello entra), **Processore** (più lento, lascia libera la scheda grafica per i giochi) | Automatico |
| Memoria grafica per l'IA | Un limite alla memoria grafica che l'IA può usare | Impostato dal preset |
| Core del processore | Meno core mantengono il PC reattivo | Impostato dal preset |
| Priorità | **Bassa** (prima il tuo lavoro) oppure **Normale** | Bassa |
| Uso massimo del processore | Un tetto all'uso del processore | Nessun limite (100%) |
| Rallenta quando | L'IA viene eseguita con la priorità più bassa, usa al massimo metà del processore e torna alla normalità alla fine. Puoi scegliere una o più opzioni: è in esecuzione un gioco o un programma a schermo intero, il PC è a batteria, un'app Portal è occupata con un'attività in tempo reale come la dettatura | Programma a schermo intero, batteria e app occupata attivi |

In **Libera la memoria quando inattivo** in **Impostazioni > Generali** scegli **Mantieni il modello caricato**, **Dopo 5 minuti** o **Dopo 10 minuti** (predefinito 10 minuti, 5 a batteria). Il modello si ricarica la prossima volta che un'app lo chiede. Tenerlo caricato risponde più in fretta, ma usa memoria di continuo.

I suggerimenti dei cursori indicano se la memoria scelta fa entrare un modello interamente nella scheda grafica, in parte nel processore o solo nel processore, e propongono un modello più piccolo quando quello attuale non entra più.

[screenshot: Impostazioni > Prestazioni]

**Risoluzione dei problemi:** risposte lente su un PC con scheda grafica di solito significano che il modello entra solo in parte nella scheda grafica. Scegli un modello più piccolo oppure aumenta la Memoria grafica per l'IA. Se il PC scatta durante l'uso, scegli Silenzioso o abbassa l'Uso massimo del processore.

---

## 8. Notifiche

**Impostazioni > Notifiche** controlla i brevi messaggi di Windows mostrati quando qualcosa richiede la tua attenzione e la finestra è chiusa. Restano silenziosi mentre in primo piano c'è un programma a schermo intero, e non si ripetono più di una volta ogni pochi minuti.

| Interruttore | Significato |
|---|---|
| Un download di modello termina o non riesce | Attivo per impostazione predefinita |
| Un'app chiede l'approvazione | Attivo per impostazione predefinita |
| Il motore IA si arresta o un controllo fallisce | Attivo per impostazione predefinita |
| Un dispositivo si abbina o si connette da fuori casa | Attivo per impostazione predefinita (gli avvisi della sezione 10) |

Ciascuno può essere disattivato singolarmente. **Ripristina i valori predefiniti** nella stessa pagina li riporta allo stato iniziale.

---

## 9. Consentire ad altri dispositivi di usare questo PC

**Impostazioni > Altri dispositivi.** Disattivato per impostazione predefinita. Quando è attivo, il tuo portatile o un altro PC della rete domestica può chiedere di usare l'IA di questo PC. Un dispositivo deve prima essere abbinato con un codice, e ogni app al suo interno richiede comunque la tua approvazione.

### Abbinare un dispositivo

1. Attiva **Consenti ad altri dispositivi di usare l'IA di questo PC**. Windows potrebbe chiedere di consentire una regola di rete.
2. Premi **Abbina un dispositivo**. Compaiono un codice e un codice QR, e **Copia link** copia un link di abbinamento.
3. Sull'altro dispositivo apri la sua app Portal (oppure Portal AI Host in modalità client, sezione 12), scegli di connetterti a un PC, poi scansiona il codice, digitalo o incolla il link.
4. Verifica che stia parlando con questo PC prima di inviare qualsiasi cosa, poi mostra il nome del PC e un'impronta da confermare.

Il codice funziona una sola volta, per 5 minuti, per un dispositivo. I tentativi errati lo esauriscono; creane uno nuovo con **Abbina un dispositivo**. **Annulla abbinamento** lo interrompe.

[screenshot: il codice di abbinamento e il codice QR]

### Dispositivi abbinati

Ogni dispositivo abbinato è elencato con l'ora dell'ultimo utilizzo e un pulsante **Rimuovi**. Rimuovere un dispositivo interrompe subito il suo accesso, e le sue richieste in corso terminano. Per tornare a collegarsi deve abbinarsi di nuovo. Possono essere abbinati al massimo **16** dispositivi.

### Altre impostazioni in questa scheda

- **Raggiungi anche questo PC da fuori casa** (il relay), vedi sezione 13. Predefinito: attivo una volta abilitati gli altri dispositivi.
- **Chiave di accesso al relay**, vedi sezione 13. Predefinito: disattivato.
- **I dispositivi possono sostituire i modelli**, vedi sezione 11. Predefinito: disattivato.

**Cosa attraversa la rete:** richieste e risposte viaggiano cifrate tra i tuoi dispositivi. La connessione sulla rete domestica usa un certificato che il dispositivo ha fissato durante l'abbinamento. Nulla va a un server nostro, salvo, facoltativamente, il relay cieco (sezione 13).

**Risoluzione dei problemi:** *Un dispositivo non trova questo PC:* verifica che siano entrambi sulla stessa rete, che questo PC sia acceso e che l'host sia in esecuzione. *Il codice è «non corretto» o «scaduto»:* creane uno nuovo. *«Non sulla tua rete domestica»:* abbina prima in casa.

---

## 10. Dispositivi su questo PC

La pagina **Dispositivi di questo PC** mostra una scheda per ogni portatile abbinato. È il posto dove vedere cosa sta facendo ciascun portatile e limitarlo.

[screenshot: Dispositivi di questo PC con due schede dispositivo]

### Cosa mostra una scheda

- Stato (In linea, Inattivo, Non in linea), percorso (Rete domestica oppure Relay (fuori casa)) e ultimo contatto.
- Cosa sta eseguendo ora e cosa è in attesa.
- Richieste e token di oggi, con una barra degli ultimi 7 giorni, per app.
- Il modello in uso, chi lo ha caricato, e la velocità in token al secondo e il tempo fino al primo byte.

### Comandi

| Comando | Cosa fa |
|---|---|
| Sospendi / Riprendi | Sospende l'intero dispositivo, oppure una delle sue app. Il portatile riceve una risposta chiara «sospeso» e usa il proprio ripiego, se ne hai impostato uno. |
| Rinomina | Cambia il nome mostrato del dispositivo |
| Limiti | Priorità, limiti giornalieri, orari silenziosi e modelli consentiti (sotto) |
| Controllo modelli (interruttore) | Permette a questo dispositivo di caricare e scaricare modelli. Vedi sezione 11 |
| Riprendi controllo | Scarica il modello caricato da questo dispositivo. Torna la scelta propria di questo PC. Nessun file di modello viene toccato. |
| Dimentica | Rimuove il dispositivo. Il suo accesso si interrompe subito e la cronologia di utilizzo viene cancellata. I modelli non vengono toccati. |

**Limiti** (sotto il pulsante **Limiti** della scheda, poi **Salva**):

- **Stessa priorità delle app di questo PC.** Disattivato (predefinito): le app proprie di questo PC passano per prime. Attivo: il dispositivo attende nella stessa coda.
- **Richieste al giorno** e **Token al giorno.** Predefinito: nessun limite. Quando sono esauriti, al dispositivo viene detto di tornare dopo mezzanotte (ora di questo PC).
- **Orari silenziosi**, con **Da** e **A** (ora di questo PC). Il dispositivo non riceve risposte in quegli orari. Predefinito: disattivato.
- **Modelli che questo dispositivo può usare.** Predefinito: tutti i modelli.

### Modalità gioco

In alto nella pagina. **Predefinito: disattivato.** Quando è attiva, le richieste degli altri dispositivi ricevono la risposta che questo PC è occupato mentre giochi, così i giochi mantengono la scheda grafica. Questo PC risulta «in gioco» quando Windows segnala un gioco a schermo intero, oppure quando un altro programma tiene il motore 3D della scheda grafica occupato al 40% o più per circa 10 secondi; quindi contano anche i giochi in finestra o in finestra senza bordi. Smette di contare circa 30 secondi dopo la fine del carico. Se i contatori grafici non sono disponibili sul tuo PC, viene usato solo il controllo a schermo intero. Il portatile ricorre allora alle proprie impostazioni.

### Avvisi

Compare una notifica di Windows quando un nuovo dispositivo si abbina e quando un dispositivo si connette tramite il relay da fuori casa. Disattivali in **Impostazioni > Notifiche > Un dispositivo si abbina o si connette da fuori casa**.

### Dati

I numeri di utilizzo sono solo conteggi (richieste e token per app e per giorno, 7 giorni conservati). Non viene salvato alcun testo di richieste o risposte. **Dimentica** elimina il file di utilizzo di quel dispositivo e nient'altro.

---

## 11. Controllo dei modelli e due modelli insieme

Per impostazione predefinita un portatile abbinato usa il modello che questo PC ha caricato. Puoi dare di più a un dispositivo.

### Controllo dei modelli

**Impostazioni > Altri dispositivi**, nella riga del dispositivo: **Controllo modelli** (predefinito: disattivato). Con il controllo attivo, il portatile può:
- caricare e scaricare i modelli installati su questo PC,
- scegliere dove viene eseguito un modello (Automatico, Scheda grafica, Memoria di sistema o Solo processore),
- scegliere quali modelli di questo PC usa ciascuna delle sue app.

Non può mai scaricare, rimuovere, controllare o importare modelli, cambiare impostazioni o toccare i tuoi file, con o senza questa concessione. Se lo disattivi, la chiamata successiva del dispositivo viene rifiutata. Rimuovere il dispositivo elimina la concessione, e abbinarlo di nuovo non la ripristina.

Il PC mostra chi ha caricato cosa: «Caricato da <dispositivo>: <modello> (scheda grafica)». **Riprendi controllo** (sulla scheda e nelle Impostazioni) scarica il modello che un dispositivo ha caricato. Torna la scelta propria di questo PC, che si carica alla richiesta successiva.

### Due modelli insieme

Con il Controllo modelli, un portatile può tenere caricato su questo PC un secondo modello, per esempio uno sulla scheda grafica e uno nella memoria di sistema.
- Il tuo modello resta nel primo slot. Il modello di un dispositivo va nel secondo, così un dispositivo non sostituisce mai il tuo modello solo per ottenere il suo.
- Ogni slot ha una propria coda, quindi due modelli possono rispondere nello stesso momento. Una richiesta che nomina il secondo modello va a lui; tutto il resto va al tuo.
- Prima di caricare, l'host conta quanto l'altro slot già usa rispetto ai tuoi limiti. Un secondo modello che non entra viene rifiutato («memoria insufficiente»). Nulla viene fermato in silenzio.
- Le tue scelte hanno sempre la precedenza. Se scegli un altro modello, ne scarichi uno, oppure il tuo modello ha bisogno della memoria, il modello del dispositivo viene fermato e il dispositivo ne viene informato.
- Il secondo modello di un dispositivo non viene mai tenuto caricato per sempre; si scarica come gli altri quando è inattivo.
- **Solo processore** esegue un modello senza livelli grafici, usando circa metà del processore; per esempio un modello ausiliario sul processore mentre un altro usa la scheda grafica.

[screenshot: la pagina Modelli con due modelli caricati da proprietari diversi]

### I dispositivi possono sostituire i modelli

**Impostazioni > Altri dispositivi > I dispositivi possono sostituire i modelli** (predefinito: disattivato). Disattivato: un dispositivo non può scaricare un modello che le tue app stanno usando. Attivo: può farlo, e le tue app caricano di nuovo il loro modello alla richiesta successiva. Con due dispositivi, il primo che ha caricato conserva il suo modello.

---

## 12. Usare il PC di casa dal portatile (modalità client)

La modalità client fa sì che Portal AI Host su un portatile usi l'IA di un altro dei tuoi PC invece di eseguire un modello da solo. Le app del portatile funzionano come al solito.

### Configurazione

1. Sul PC di casa attiva **Consenti ad altri dispositivi di usare l'IA di questo PC** e premi **Abbina un dispositivo** (sezione 9). Premi **Copia link** e invia il link al portatile.
2. Sul portatile apri **Impostazioni > PC di casa** (oppure scegli **Usa il mio PC di casa** nella finestra di benvenuto).
3. In **Dove viene eseguita l'IA** scegli **Usa il mio PC di casa**. Si apre una finestra **Usa il mio PC di casa**. Incolla il link con **Incolla link**, premi **Avanti**, controlla il nome del PC e l'impronta, poi **Abbina**. Assegna un nome al portatile.
4. Home ora mostra il nome del PC e uno stato: **Connesso - rete domestica**, **Connesso - tramite relay**, **Connessione in corso...** oppure **Non raggiungibile**.

[screenshot: la finestra di abbinamento sul portatile]

### Impostazioni (Impostazioni > PC di casa)

| Impostazione | Scelte | Predefinito |
|---|---|---|
| Dove viene eseguita l'IA | Questo PC / Usa il mio PC di casa | Questo PC |
| Se il PC di casa non è raggiungibile | **Nessuno** (le app usano la propria IA integrata) / **Usa il modello di questo portatile** (si carica quando serve) | Nessuno |
| Connessione fuori casa | **Automatica** / **Manuale** (allora un interruttore **Usa relay** vale solo per questa sessione e si ripristina alla chiusura dell'app) | Automatica |

- La prima volta che il PC di casa non è raggiungibile ti viene chiesto una volta cosa fare; puoi cambiarlo qui in qualsiasi momento.
- **Abbina PC** abbina di nuovo; **Dissocia** dimentica il PC (modelli e download restano come sono); **Cambia PC** (in Home) apre queste impostazioni; **Esegui IA qui** (in Home) esce dalla modalità client e conserva l'abbinamento salvato.
- Con il Controllo modelli concesso dal PC di casa, compare **Modelli su <nome PC>** con i pulsanti Carica, Scarica e posizionamento, più **Modello del PC per ogni app**. Senza concessione, la pagina dice: «Chiedi al proprietario di <nome PC> di attivare il Controllo modelli per questo portatile».
- Un portatile in modalità client non accetta connessioni da altri dispositivi e non può fare da tramite verso il tuo PC di casa.

### Cosa succede ai modelli propri di questo portatile

Entrando in modalità client, il modello caricato sul portatile viene scaricato per liberare memoria. File dei modelli, download e cartella dei modelli non vengono toccati. Uscendo dalla modalità client, il modello si ricarica quando richiesto.

### Risoluzione dei problemi

- **Non raggiungibile:** verifica che il PC di casa sia acceso e sulla stessa rete, oppure attiva il relay.
- **«Questo portatile è stato rimosso dal PC»:** il PC di casa lo ha rimosso. Scegli **Abbina di nuovo**.
- **«L'identità di <PC> è cambiata»:** il certificato del PC non corrisponde più a quello salvato dal portatile. Se non te lo aspettavi, non abbinare di nuovo. Altrimenti dissocia, poi abbina di nuovo.
- **L'abbinamento da fuori casa non riesce:** abbina prima a casa; il link di abbinamento deve puntare a un indirizzo della tua rete domestica.
- **In pausa, orari silenziosi, un gioco in corso o un limite giornaliero esaurito:** il PC comunica al portatile il motivo; il portatile usa il proprio ripiego.

**Dove viene archiviato:** il portatile conserva il suo abbinamento nella cartella dati dell'host, sotto `client\`. La chiave del dispositivo e i token sono protetti per il tuo utente Windows. **Dissocia** li rimuove. I backup di quella cartella non possono essere ripristinati con un altro utente Windows.

---

## 13. Fuori casa: il relay e la chiave di accesso al relay

Il relay permette a un dispositivo abbinato di raggiungere il tuo PC da qualsiasi luogo. **Impostazioni > Altri dispositivi > Raggiungi anche questo PC da fuori casa.**

- Il tuo PC si connette verso l'esterno a un piccolo servizio di relay. Il relay inoltra solo byte cifrati che non può leggere, e non conserva alcun contenuto. Ogni dispositivo deve comunque essere abbinato e firma ogni richiesta.
- La riga di stato mostra **Raggiungibile da fuori casa**, **Solo rete domestica**, **Connessione al relay in corso...** oppure **Impossibile contattare il relay**.
- Funziona sulla porta web abituale, quindi la maggior parte delle reti aziendali lo consente.
- **Dispositivi aziendali:** i contenuti di lavoro finiranno sul tuo PC personale. Verifica prima le regole del tuo datore di lavoro.

### Chiave di accesso al relay

**Impostazioni > Altri dispositivi > Chiave di accesso al relay** (predefinito: disattivato). Quando è attiva, solo i dispositivi abbinati con la chiave inclusa nel codice di abbinamento possono raggiungere questo PC tramite il relay, impedendo a estranei di occupare gli slot del relay del tuo PC.

- Attivala **prima di abbinare**. I dispositivi abbinati in precedenza devono abbinarsi di nuovo per usare il relay (la rete domestica continua a funzionare).
- **Nuova chiave** crea una chiave nuova dopo una conferma. Ogni dispositivo abbinato deve abbinarsi di nuovo per usare il relay; a casa non cambia nulla. Rimuovere un dispositivo non cambia la chiave, quindi usa **Nuova chiave** dopo aver rimosso un dispositivo di cui non ti fidi.
- Se il file della chiave non può essere usato, il relay resta disattivato anziché aperto, e la rete domestica continua a funzionare. Un file di chiave danneggiato viene messo da parte con un nome `.bad` e non viene mai sovrascritto.
- Se l'interruttore non resta attivo, il servizio relay potrebbe dover essere aggiornato dalla sua parte. Disattivalo di nuovo.

**Limiti:** il relay limita la velocità e la quantità che un dispositivo può inviare, così un solo dispositivo non può saturare il servizio. L'uso normale è ben al di sotto di questi limiti.

---

## 14. Privacy e rapporti di arresto anomalo

- **Nessuna analisi né tracciamento dell'uso.** Solo un rapporto di arresto anomalo può lasciare il tuo PC, e solo se lo lasci attivo.
- **Il tuo testo resta sui tuoi dispositivi.** Richieste e risposte non vengono mai salvate né registrate dall'host. I registri contengono solo conteggi, tempi e stati.
- **I rapporti di arresto anomalo sono attivi per impostazione predefinita, e puoi disattivarli.** Se la finestra o l'host in background si blocca, un breve rapporto ci aiuta a correggere il problema. Disattivalo in qualsiasi momento in **Impostazioni > Privacy > Invia rapporti di arresto anomalo**; ha effetto subito. La finestra di benvenuto offre la stessa scelta, selezionata per impostazione predefinita, prima che venga inviato qualsiasi cosa.
  - **Inviato:** il tipo di errore e dove si è verificato (nomi di funzioni e moduli), le versioni di app e Windows, se si tratta di una build dello Store, e se l'errore proviene dalla finestra o dall'host.
  - **Mai inviato:** richieste o risposte, qualsiasi cosa digiti, nomi di file o percorsi di cartelle, il nome del computer o dell'utente, quali app usano l'host, o il tuo indirizzo IP. Il testo dell'errore viene eliminato.
  - **Conservati per 90 giorni**, poi eliminati.
  - **Cosa viene inviato** (Impostazioni > Privacy) apre la pagina sulla privacy.
- **Tutto è cifrato** quando lascia questo PC verso un altro dei tuoi dispositivi.
- **Informativa sulla privacy:** Informazioni > **Informativa sulla privacy**.
- Domande, oppure richiesta di eliminare un rapporto: mindthaapp@gmail.com.

[screenshot: Impostazioni > Privacy con l'interruttore dei rapporti di arresto anomalo]

---

## 15. Dove sono archiviati i dati, backup e rimozione

Tutto si trova nella cartella dati dell'app. **Informazioni > Apri cartella dati** la apre.

| Cosa | Dove (nella cartella dati) | Fare un backup? |
|---|---|---|
| Modelli scaricati | `models\`, oppure `Portal AI Host Models\` nella cartella scelta in Impostazioni | Facoltativo. Possono essere scaricati di nuovo. Sono grandi. |
| Impostazioni | `settings.json` | Sì |
| App consentite e loro token | L'archivio app dell'host | Non trasferibile (protetto per il tuo utente Windows) |
| Dispositivi abbinati, limiti, statistiche di utilizzo | L'archivio dispositivi dell'host e un piccolo file di statistiche per dispositivo | Non trasferibile |
| Abbinamento del portatile (modalità client) | `client\` | Non trasferibile. Abbina di nuovo su un nuovo PC. |
| Registri | `logs\` (a rotazione, limitati a 10 MB) | No |
| File di individuazione per le app | `discovery\host.json` | No |

**Backup:** chiudi Portal AI Host, poi copia la cartella dati. Impostazioni e modelli possono essere ripristinati ricopiandoli. Permessi delle app, abbinamenti dei dispositivi e token sono protetti per il tuo utente Windows e per il tuo PC, quindi su un nuovo PC consenti di nuovo le app e abbina di nuovo i dispositivi.

**Rimozione:**
- *Un modello:* **Modelli > Rimuovi**. Vengono eliminati solo i file scaricati dall'host.
- *Residui:* **Modelli > Libera spazio**.
- *Un dispositivo:* **Dimentica** (Dispositivi di questo PC) oppure **Rimuovi** (Impostazioni).
- *L'accesso di un'app:* **Rimuovi accesso** (Dispositivi e app).
- *Tutto:* disinstalla l'app. Una cartella di modelli personalizzata che hai scelto resta al suo posto; elimina tu la sua cartella `Portal AI Host Models` se vuoi che sparisca.

L'app non elimina mai nulla che non ha creato, non sovrascrive mai i tuoi file, e un modello aggiunto da te viene rimosso solo come copia propria dell'host (il tuo file originale resta intatto).

---

## 16. Scorciatoie da tastiera

| Tasti | Azione |
|---|---|
| Ctrl+1 | Home |
| Ctrl+2 | Modelli |
| Ctrl+3 | Dispositivi e app |
| Ctrl+4 | Provalo |
| Ctrl+, | Impostazioni |
| F1 | Informazioni |
| Ctrl+Invio | Chiedi, in Provalo |
| Esc | Chiude Novità o una finestra di dialogo |

L'elenco si trova anche nel menu dell'app > **Scorciatoie da tastiera**.

Aspetto: **Impostazioni > Aspetto** offre Tema (predefinito: l'impostazione di Windows), Materiale finestra (Mica), Angoli (Arrotondati), Densità (Standard), Dimensione testo (Normale) e Animazioni (Segui Windows). Il contrasto elevato di Windows usa sempre colori di sistema pieni.

---

## 17. Risoluzione dei problemi e FAQ

**L'host dice che non è in esecuzione.** Premi **Avvia Portal AI Host** in Home. Se non riesce ad avviarsi, riavvia il PC; se anche questo non basta, reinstalla dallo Store.

**«Un altro utente Windows su questo PC sta già eseguendo Portal AI Host.»** Chiedi a quell'utente di chiuderlo, oppure accedi come lui.

**Le risposte sono lente.** Vedi la sezione 7. Un modello troppo grande per la memoria grafica viene eseguito in parte sul processore.

**Un'app dice che nessuna IA è disponibile.** Verifica che l'app sia consentita (Dispositivi e app), che non sia sospesa, e che un modello sia installato e non troppo grande per il PC.

**La prima risposta è lenta, le successive sono veloci.** Il modello era in pausa. Si carica alla prima richiesta. Scegli **Mantieni il modello caricato** in Impostazioni > Generali per evitarlo.

**I miei dati vengono inviati da qualche parte?** No. Richieste e risposte restano sui tuoi dispositivi. Possono uscire solo i rapporti di arresto anomalo (sezione 14), e puoi disattivarli.

**Posso usarlo senza Internet?** Sì, una volta scaricato un modello.

**Costa qualcosa?** No.

**Quale modello devo scegliere?** Quello consigliato. Scegline uno più piccolo se il PC ti sembra lento, uno più grande se vuoi risposte migliori e hai la memoria.

**Il download è bloccato su «Verifica del download».** Attendi. I file grandi richiedono un minuto per essere verificati.

**L'abbinamento dice «troppi dispositivi».** Il limite è di 16 dispositivi. Rimuovine uno.

**Dov'è l'icona nell'area di notifica?** Fai clic sulla freccia vicino all'orologio per mostrare le icone nascoste.

Se continui ad avere problemi, usa **Informazioni > Copia versione**, scrivi a mindthaapp@gmail.com e incolla la versione nel tuo messaggio.

---

## 18. In arrivo

Sono previsti e non ancora presenti nell'app.

- **Categorie di modelli nella pagina Modelli:** raggruppati per attività (chat e scrittura, programmazione, ragionamento, visione, embeddings, voce), con filtri e ricerca.
- **Generazione di immagini e video** su questo PC, incluso un «regista» che concatena le inquadrature in un video. I modelli video compariranno come «Prossimamente» finché il motore non esisterà.
- **Scelta del modello che risponde**, e modelli che si chiedono aiuto a vicenda, in AI Portal.
- **Versione per macOS.**

---

## 19. Supporto

- **Feedback e idee:** Informazioni > **Invia feedback o suggerisci un'idea**.
- **Domande su supporto e privacy:** mindthaapp@gmail.com.
- **Versione:** Informazioni la mostra, e **Copia versione** la mette negli appunti per il tuo messaggio.
- **Licenze:** Portal AI Host include llama.cpp (licenza MIT), che esegue i modelli sul tuo PC. I modelli vengono scaricati con le proprie licenze (Apache 2.0 o MIT). Vedi Informazioni.
