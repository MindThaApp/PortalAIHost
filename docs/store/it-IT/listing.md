# Scheda Store (italiano, it-IT)

Traduzione della scheda Store inglese (listing-en.md). Solo le sezioni visibili all'utente. Nomi di prodotti e link invariati.

## Nome dell'app

Portal AI Host

## Descrizione breve

Esegui un modello di IA locale sul tuo PC e condividilo con le tue app Portal. Con un clic viene scelto il modello migliore per il tuo hardware, scaricato e tenuto pronto. Il tuo testo resta su questo PC: nessun account, nessun cloud, nessun abbonamento.

## Descrizione

Portal AI Host dà al tuo PC un proprio modello di IA e permette alle altre app di usarlo.

Invece di far scaricare a ogni app il proprio modello o di inviare il tuo testo a un servizio cloud, Portal AI Host conserva un unico modello sul tuo PC e lo condivide con le app che autorizzi. Voice Portal e le altre app Portal possono così creare riassunti, verbali e risposte con un modello più grande e più potente di quello che potrebbero includere da sole.

Semplice da configurare
- Al primo avvio l'app controlla il tuo PC (memoria, scheda grafica, spazio libero su disco) e consiglia il modello migliore che vi funzioni bene, con una spiegazione chiara.
- Un clic lo scarica. I download si possono mettere in pausa e riprendere, e ogni file viene verificato prima di essere usato.
- Non c'è nient'altro da installare: il motore è incluso. Niente Python, niente riga di comando, nessuno strumento aggiuntivo.

Privato per progettazione
- Il modello gira su questo PC. Richieste e risposte non lasciano mai il PC.
- Nessun account e nessun accesso.
- Nessuna analisi. I rapporti di arresto anomalo facoltativi non includono mai i tuoi file né ciò che digiti, e puoi disattivarli. Le tue richieste non vengono mai salvate né registrate.
- Il server ascolta solo su questo PC (loopback). Gli altri computer della rete non possono raggiungerlo.

Tu mantieni il controllo
- Un'app deve chiedere il permesso prima di usare il server, e sei tu a decidere: consentire, negare o decidere più tardi. Puoi revocare l'accesso di un'app in qualsiasi momento.
- Il server indica se un'app è verificata (il suo editore), così sai chi sta chiedendo.
- Scegli dove vengono archiviati i modelli ed eliminane quelli che non ti servono più. Il server elimina solo i file che ha scaricato lui stesso.
- Avvia con Windows è facoltativo e disattivato finché non lo attivi.

Funziona con il tuo hardware
- Usa la scheda grafica tramite Vulkan (AMD, NVIDIA e Intel) e ricorre al processore quando non c'è una GPU adatta.
- Libera la memoria quando il modello è inattivo, oppure lo mantiene caricato se preferisci.

Per gli sviluppatori
- Il server offre un'API standard compatibile con OpenAI su 127.0.0.1 (chat completions con streaming, embeddings ed elenco dei modelli), quindi le librerie client esistenti funzionano. Ogni app richiede prima l'approvazione dell'utente.

I modelli vengono scaricati dalle pagine dei loro editori su Hugging Face. Ogni modello ha una propria licenza; l'app offre solo modelli le cui licenze consentono l'uso commerciale (Apache 2.0 o MIT).

## Funzionalità (Partner Center, «Funzionalità del prodotto», massimo 20, 200 caratteri ciascuna)

1. Esegue un modello di IA in locale sul tuo PC, senza cloud
2. Un solo modello condiviso da tutte le tue app Portal
3. Consiglia il modello migliore per il tuo hardware
4. Download con un clic, con pausa, ripresa e controllo dei file
5. Usa la scheda grafica (Vulkan), con ripiego automatico sul processore
6. Nessun account, nessun accesso, nessun abbonamento
7. Nessuna analisi; i rapporti di arresto anomalo sono facoltativi e depurati; le richieste non vengono mai salvate né registrate
8. Ascolta solo su questo PC (loopback)
9. Le app devono chiedere il permesso; tu consenti, neghi o revochi l'accesso
10. Indica se un'app richiedente è verificata
11. Libera la memoria quando è inattivo, oppure mantiene il modello caricato
12. Scegli la cartella per i tuoi modelli
13. Avvio facoltativo con Windows, con icona nell'area di notifica
14. Riquadro «Provalo» per verificare che il modello funzioni
15. API locale compatibile con OpenAI per gli sviluppatori
16. Condivisione equa quando più app chiedono contemporaneamente
17. Si ripristina da solo se il motore si arresta

## Termini di ricerca (parole chiave, 7)

1. IA locale
2. IA offline
3. IA privata
4. LLM
5. modello di IA
6. modello linguistico
7. server IA

Nessun nome di prodotti di terzi nelle parole chiave (politica Store 10.1.3).

## Novità di questa versione (1.0)

Prima versione. Scarica con un clic un modello di IA locale e condividilo in modo privato con le tue app Portal, sul tuo PC.

## Requisiti di sistema (testo della scheda)

> Funziona al meglio con 16 GB di memoria e una scheda grafica con 8 GB di memoria video. Senza una scheda grafica adatta, il modello gira sul processore, che è più lento. I PC Arm64 eseguono il modello sul processore. I modelli richiedono da 2 a 20 GB di spazio libero su disco.

Minimo: Windows 11 (versione 21H2, build 22000) o successiva, x64 o Arm64. Memoria: 8 GB. Spazio libero su disco: 5 GB (l'app in sé è piccola; i modelli occupano da 2 a 20 GB, quelli facoltativi più grandi fino a circa 140 GB).

Consigliato: 16 GB di memoria o più (32 GB o più per i modelli grandi). Una scheda grafica compatibile con Vulkan con 8 GB di memoria video o più (AMD, NVIDIA o Intel). 25 GB di spazio libero su disco.
