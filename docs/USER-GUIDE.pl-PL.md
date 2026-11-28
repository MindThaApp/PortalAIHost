# Portal AI Host: podręcznik użytkownika

Portal AI Host uruchamia model AI na Twoim komputerze i udostępnia go Twoim aplikacjom Portal. Twoje pytania i pliki
pozostają na Twoich własnych urządzeniach. Ten podręcznik opisuje każdą część aplikacji, to, co robią jej ustawienia,
i co zrobić, gdy coś pójdzie nie tak.

Miejsca, w których powinien znaleźć się obraz, są oznaczone jako `[screenshot: ...]` (zrzut ekranu: ...).

## Spis treści

1. Szybki start
2. Wymagania systemowe, instalacja, aktualizacja, odinstalowanie
3. Poruszanie się po aplikacji
4. Modele
5. Aplikacje i uprawnienia
6. Wypróbuj
7. Ustawienia wydajności
8. Powiadomienia
9. Udostępnianie tego komputera innym urządzeniom
10. Urządzenia na tym komputerze
11. Sterowanie modelami i dwa modele naraz
12. Korzystanie z domowego komputera z laptopa (tryb klienta)
13. Poza domem: przekaźnik i klucz dostępu do przekaźnika
14. Prywatność i raporty o awariach
15. Gdzie przechowywane są dane, kopia zapasowa i usuwanie
16. Skróty klawiszowe
17. Rozwiązywanie problemów i najczęstsze pytania
18. Planowane funkcje
19. Pomoc techniczna

---

## 1. Szybki start

1. Zainstaluj Portal AI Host ze Sklepu Microsoft i otwórz go.
2. Okno powitalne sprawdzi Twój komputer (pamięć, kartę graficzną, wolne miejsce na dysku) i zaproponuje jeden model.
   Wybierz **Start z zalecanymi ustawieniami** albo **Pobierz**, aby samodzielnie wybrać model. **Wybiorę później** pomija
   ten krok.
3. Szukaj ikony przy zegarze w obszarze powiadomień. Portal AI Host czeka tam cicho. Kliknij ją, aby zobaczyć krótki status.
4. Otwórz **Wypróbuj** i zadaj krótkie pytanie, aby sprawdzić, czy model działa.
5. Za pierwszym razem, gdy aplikacja Portal zechce użyć AI, pojawi się pytanie, czy na to zezwolić.

[screenshot: okno powitalne z zalecanym modelem]

Na laptopie, który ma korzystać z AI Twojego domowego komputera, wybierz w oknie powitalnym **Użyj mojego komputera domowego**.
Zobacz sekcję 12.

**Bezpłatnie czy płatnie:** Portal AI Host jest bezpłatny. Żadna funkcja opisana w tym podręczniku nie wymaga konta, logowania
ani zakupu.

---

## 2. Wymagania systemowe, instalacja, aktualizacja, odinstalowanie

### Wymagania

| | Minimum | Zalecane |
|---|---|---|
| Windows | Windows 11 (21H2, kompilacja 22000) lub nowszy, x64 lub Arm64 | Tak samo |
| Pamięć | 8 GB | 16 GB lub więcej (32 GB lub więcej dla dużych modeli) |
| Grafika | Brak (model działa na procesorze, wolniej) | Karta graficzna z obsługą Vulkan i 8 GB pamięci wideo lub więcej (AMD, NVIDIA lub Intel) |
| Wolne miejsce na dysku | 5 GB | 25 GB |

Każdy model ma od 2 do 20 GB. Komputery z Arm64 uruchamiają model na procesorze. Połączenie z internetem jest potrzebne tylko
do pobierania modeli i do działania przekaźnika; gdy model jest już na komputerze, działa bez internetu.

### Instalacja

Zainstaluj aplikację ze Sklepu Microsoft. Nic więcej nie jest potrzebne: ani Python, ani wiersz poleceń, ani inne
oprogramowanie. Silnik AI jest częścią aplikacji.

### Start razem z Windows

**Ustawienia > Ogólne > Uruchom razem z Windows** (domyślnie wyłączone, z wyjątkiem przycisku „Start z zalecanymi
ustawieniami” w oknie powitalnym, który to włącza). Gdy opcja jest włączona, Portal AI Host czeka w obszarze powiadomień
po zalogowaniu, więc aplikacje mogą z niego od razu korzystać.
- Jeśli przełącznik informuje, że jest dostępny tylko w zainstalowanej aplikacji, uruchamiasz kopię deweloperską.
- Jeśli wyłączyłeś tę opcję w Menedżerze zadań (Aplikacje startowe), włącz ją ponownie tam.
- Jeśli opcja jest ustawiona przez Twoją organizację, przełącznik jest zablokowany.

### Aktualizacja

Sklep aktualizuje aplikację. Po aktualizacji otwiera się jednorazowo okno **Co nowego**. Możesz je otworzyć ponownie z menu
aplikacji. Modele po aktualizacji pozostają na swoim miejscu.

### Odinstalowanie

Usuń aplikację w **Ustawienia Windows > Aplikacje**. Modele w domyślnym folderze są przechowywane w danych aplikacji
i usuwane razem z nią. Własny folder modeli, który wybrałeś, nie jest ruszany. Przed odinstalowaniem, jeśli chcesz coś
zachować, zobacz sekcję 15.

---

## 3. Poruszanie się po aplikacji

Po lewej stronie znajduje się pięć stron.

| Strona | Do czego służy | Skrót |
|---|---|---|
| Start | Status, aktywny model, zużycie pamięci, aplikacja korzystająca właśnie z AI | Ctrl+1 |
| Modele | Pobieranie, używanie i usuwanie modeli | Ctrl+2 |
| Urządzenia i aplikacje | Zezwalanie na aplikacje Portal lub ich usuwanie, wstrzymywanie, ustawianie priorytetu | Ctrl+3 |
| Wypróbuj | Zadanie modelowi jednego krótkiego pytania | Ctrl+4 |
| Ustawienia | Wszystko, co można skonfigurować | Ctrl+, |

Szósta strona, **Urządzenia tego komputera**, pokazuje laptopy i telefony sparowane z tym komputerem (zobacz
sekcję 10). Menu aplikacji zawiera Ustawienia, Skróty klawiszowe, Co nowego i Informacje.

Na stronie **Start** widać, czy AI jest gotowe, odpowiada, przygotowuje model, odpoczywa (model budzi się, gdy aplikacja o to
poprosi) albo nie ma jeszcze modelu. Sekcja **TERAZ** pokazuje, która aplikacja dostaje odpowiedź i mniej więcej ile słów na
sekundę. Pokazuje też, ile pamięci i pamięci graficznej zużywa model, oraz przycisk **Wczytaj teraz** / **Wyładuj teraz**.
Sekcja **DZIŚ** liczy zapytania każdej aplikacji. Karta stanu mówi „Wszystko w porządku” albo wymienia, co wymaga uwagi,
a przy każdej pozycji jest jednoklikowa naprawa, np. Wczytaj model, Uruchom silnik ponownie, Sprawdź pliki albo Otwórz
ustawienia wydajności.

[screenshot: strona Start podczas korzystania modelu przez aplikację]

Ikona w obszarze powiadomień otwiera małe okno: model, pamięć, ostatnie zapytanie i kolejka, z przyciskami **Otwórz
aplikację** i **Wyładuj model**.

[screenshot: okno z obszaru powiadomień]

Jeśli program hosta nie działa, Start pokazuje przycisk **Uruchom Portal AI Host**. Jeśli zamknął się nieoczekiwanie więcej niż
raz, pojawia się pasek **Tryb awaryjny**, który oznacza, że aplikacja uruchomiła się z ostrożnymi ustawieniami. Twoje
aplikacje nadal działają; jeśli problem się powtarza, uruchom ponownie komputer.

---

## 4. Modele

Model to AI, które wykonuje pracę. **Modele** (Ctrl+2) pokazuje, co masz i co możesz dodać.

### Polecany model

Pierwsze uruchomienie i strona Start polecają „najlepszy model dla tego komputera” z prostym uzasadnieniem, opartym na pamięci,
pamięci graficznej i wolnym miejscu na dysku. Gdy dostępny jest lepszy model, pojawia się pasek **Dostępny lepszy model**.
Obecny model zostaje, dopóki nowy nie będzie gotowy, a to, kiedy usunąć stary, wybierasz Ty.

### Pobieranie modelu

1. Otwórz **Modele**. Wybierz model w sekcji **Modele do dodania**.
2. Naciśnij **Pobierz**. Pasek pokazuje postęp i pozostały czas.
3. **Wstrzymaj** i **Wznów** działają w dowolnym momencie. **Zatrzymaj** odrzuca już pobraną część (po potwierdzeniu).
4. Host sprawdza pobrane pliki, zanim ich użyje.
5. Po zakończeniu naciśnij **Użyj tego modelu**.

Pobieranie można wznowić po ponownym uruchomieniu, a każdy plik jest sprawdzany osobno. Pobieranie wymaga wystarczającej ilości
wolnego miejsca na dysku; jeśli jest go za mało, strona podaje, ile brakuje, i proponuje **Zmień folder w ustawieniach**.
Komputer nie przechodzi w stan uśpienia podczas pobierania (Ustawienia > Wydajność, domyślnie włączone).

[screenshot: strona Modele z trwającym pobieraniem]

Model oznaczony jako „Ten model może być za duży dla tego komputera” można pobrać, ale może działać wolno lub się nie zmieścić.

### Szczegóły, sprawdzanie plików, ponowne pobieranie, usuwanie

- **Szczegóły** pokazują, do czego model się nadaje, na co zwrócić uwagę, ile pamięci potrzebuje, jego licencję, kiedy był
  ostatnio używany i ile zajmuje miejsca.
- **Sprawdź pliki** ponownie weryfikuje model na dysku. Nic nie usuwa. Uszkodzony model jest oznaczony jako **Uszkodzony**.
- **Pobierz ponownie** (dla uszkodzonego modelu) usuwa uszkodzoną kopię i pobiera model od nowa. Nic innego nie jest usuwane.
- **Usuń** usuwa model z komputera po potwierdzeniu, które mówi, ile miejsca się zwolni. Jeśli model jest używany, host
  najpierw go wyładowuje i pyta, czego użyć dalej. Usuwane są wyłącznie pliki pobrane przez host.

### Dodawanie własnego modelu

**Dodaj własny model...** pozwala wybrać plik `.gguf`. Host tworzy własną kopię w folderze modeli. Oryginał nigdy nie jest
zmieniany ani przenoszony. Wiersz jest oznaczony jako **Dodany przez Ciebie**, a host nie może oszacować, ile pamięci model
potrzebuje, więc wypróbuj go i sprawdź. **Usuń** przy takim modelu usuwa tylko kopię hosta.

### Zwalnianie miejsca

**Zwolnij miejsce** wyświetla pozostałości, które sama aplikacja utworzyła: niedokończone pobierania, których nic nie wznawia,
stare kopie pozostałe po przeniesieniu modeli oraz modele uznane przez sprawdzenie za uszkodzone. Przed usunięciem musisz
potwierdzić. Modele, których używasz, i pliki, które sam dodałeś, nigdy nie są na tej liście. Niedokończone pobierania, które
można wznowić, są przechowywane przez tydzień.

### Folder modeli i przenoszenie modeli

**Ustawienia > Folder modeli** określa, dokąd trafiają nowe pobierania. Zostaw pole puste, aby użyć domyślnego folderu, albo
wpisz pełną ścieżkę, np. `D:\Modele`, i naciśnij Enter. Modele już pobrane nie są przenoszone ani usuwane. Pozostają tam,
gdzie są, i nadal są widoczne. Aby je przenieść, zaakceptuj **Przenieś moje modele tutaj**. Są kopiowane, sprawdzane, a dopiero
potem usuwane są stare kopie. Model w użyciu zostaje wyładowany na czas tej operacji, a **Anuluj przenoszenie** zostawia
wszystko tam, gdzie było.

### Budżet pamięci i paski

**Paski Budżet pamięci** na stronie Modele pokazują pamięć RAM i pamięć graficzną: ile zajmują załadowane modele, ile jest
zarezerwowane podczas ładowania i ile jest wolne w ramach Twoich limitów (zobacz sekcję 7).
Ładowanie, które przekroczyłoby Twój limit pamięci, zostaje odrzucone z czytelnym powodem, zanim cokolwiek zostanie zatrzymane.

[screenshot: paski Budżet pamięci z dwoma załadowanymi modelami]

### Umiejscowienie: karta graficzna, pamięć lub tylko procesor

O tym, gdzie działa model, decyduje **Ustawienia > Wydajność > Uruchamiaj na** (Automatycznie, Karta graficzna, Procesor).
Model, który nie mieści się na karcie graficznej, działa częściowo na procesorze (wolniej). O umiejscowieniu dla konkretnego
modelu zobacz też sekcję 11, która zawiera opcję **Tylko procesor**.

### Ograniczenia a wersje bezpłatne i płatne

- Wszystkie modele na liście są bezpłatne do użytku komercyjnego (licencje Apache 2.0 lub MIT); każdy model pokazuje swoją
  licencję.
- Modele są pobierane ze stron ich wydawców na Hugging Face. Host akceptuje wyłącznie zaufaną, podpisaną listę modeli.
  Jeśli nie może jej wczytać, pojawia się pasek **Lista modeli niedostępna**; modele już obecne na komputerze nadal działają.

### Rozwiązywanie problemów

- *Brak modeli do pobrania:* sprawdź połączenie z internetem i naciśnij **Spróbuj ponownie** na stronie Start.
- *Pobieranie się zatrzymuje:* naciśnij **Wznów**; jeśli brakuje miejsca, zwolnij je albo zmień folder.
- *Model „może być za duży”:* wybierz mniejszy albo zmniejsz obciążenie w Ustawienia > Wydajność.

---

## 5. Aplikacje i uprawnienia

Otwórz **Urządzenia i aplikacje** (Ctrl+3).

- **Czekają na Twoją odpowiedź:** aplikacja, która poprosiła o użycie AI, pojawia się tutaj i jako pytanie („Zezwolić
  Voice Portal na użycie AI na tym komputerze?”). Wybierz **Zezwól**, **Nie zezwalaj** albo **Zdecyduję później**. Pytanie
  pokazuje, czy aplikacja jest zweryfikowaną aplikacją Portal, zainstalowaną aplikacją, która nie jest aplikacją Portal, czy
  niezweryfikowaną (wtedy podaje, skąd jest uruchamiana; zezwalaj tylko wtedy, gdy sam ją uruchomiłeś).
- **Aplikacje, które mogą korzystać z hosta AI:** każda dozwolona aplikacja pokazuje dzisiejsze zapytania i datę ostatniego
  użycia.
  - **Wstrzymaj** odrzuca nowe zapytania, dopóki nie wznowisz; aplikacja zachowuje dostęp.
  - **Priorytet** (Wysoki, Normalny, Niski) decyduje, kto pierwszy dostaje odpowiedź, gdy kilka aplikacji pyta naraz.
    Zapytania interaktywne idą przed pracą wsadową, a zapytanie, które długo czeka, przesuwa się wyżej, więc aplikacja o niskim
    priorytecie nigdy nie zostaje bez odpowiedzi.
  - **Usuń dostęp** zatrzymuje aplikację, dopóki nie zezwolisz ponownie. **Cofnij** pojawia się na chwilę.

Aplikacje muszą zapytać, zanim będą mogły użyć hosta. Host nasłuchuje tylko na tym komputerze, chyba że włączysz udostępnianie
innym urządzeniom (sekcja 9).

[screenshot: strona Urządzenia i aplikacje z jedną czekającą i jedną dozwoloną aplikacją]

Dla deweloperów: host oferuje lokalne API zgodne z OpenAI pod adresem `http://127.0.0.1:41870` (uzupełnianie czatu ze
strumieniowaniem, osadzenia, lista modeli). Zobacz `api-v1.md`.

**Gdzie to jest przechowywane:** dozwolone aplikacje i ich tokeny znajdują się w folderze danych hosta, chronionym dla Twojego
użytkownika Windows. Usunięcie dostępu aplikacji usuwa jej token.

---

## 6. Wypróbuj

**Wypróbuj** (Ctrl+4) sprawdza, czy model działa. Wpisz krótkie pytanie i naciśnij **Zapytaj** (albo Ctrl+Enter). **Zatrzymaj**
przerywa odpowiedź, **Wyczyść** opróżnia stronę. Podpowiadane są przykładowe pytania. Strona pokazuje, ile trwały pierwsze słowa,
oraz szybkość w tokenach na sekundę. Nic, co tu wpiszesz, nie jest zapisywane.

[screenshot: Wypróbuj z odpowiedzią i linią czasu]

---

## 7. Ustawienia wydajności

**Ustawienia > Wydajność** nie pozwalają AI spowalniać komputera. Zmiany działają od razu; model, który już się nie mieści, jest
ponownie ładowany z nowymi limitami albo wyładowywany (pozostaje na dysku). **Przywróć domyślne** przywraca sekcję.

| Ustawienie | Co robi | Domyślnie |
|---|---|---|
| Wydajność | Gotowy profil: **Cichy** (około połowy prędkości, komputer pozostaje responsywny), **Zrównoważony** (dobra prędkość, Twoje programy mają pierwszeństwo), **Szybki** (najszybsze odpowiedzi, inne programy mogą zwolnić), **Własny** | Zrównoważony |
| Maksymalna pamięć dla AI | AI nigdy nie użyje więcej pamięci niż to | 80% pamięci komputera |
| Zawsze zostaw wolne | Pamięć zachowywana dla innych programów | Ustawiane przez profil |
| Uruchamiaj na | **Automatycznie** (karta graficzna, gdy to pomaga), **Karta graficzna** (najszybciej, jeśli model się mieści), **Procesor** (wolniej, ale karta graficzna jest wolna dla gier) | Automatycznie |
| Pamięć graficzna dla AI | Limit pamięci graficznej, którą może zająć AI | Ustawiane przez profil |
| Rdzenie procesora | Mniej rdzeni utrzymuje komputer w gotowości | Ustawiane przez profil |
| Priorytet | **Niski** (Twoja praca najpierw) lub **Normalny** | Niski |
| Maksymalne użycie procesora | Górny limit użycia procesora | Bez limitu (100%) |
| Zwalniaj, gdy | AI działa z najniższym priorytetem, używa co najwyżej połowy procesora i wraca do normy po zakończeniu. Możesz wybrać dowolne z: uruchomiona gra lub program pełnoekranowy, komputer na baterii, zajęta aplikacja Portal wykonująca pracę na żywo, np. dyktowanie | Włączone: program pełnoekranowy, bateria i zajęta aplikacja |

W **Ustawienia > Ogólne**, w opcji **Zwalniaj pamięć, gdy nieaktywne**, wybierz **Zachowaj model załadowany**, **Po 5 minutach**
albo **Po 10 minutach** (domyślnie 10 minut, 5 na baterii). Model ładuje się ponownie, gdy następnym razem poprosi o to aplikacja.
Trzymanie modelu załadowanego daje szybsze odpowiedzi, ale zużywa pamięć cały czas.

Podpowiedzi przy suwakach mówią, czy wybrana pamięć mieści model w całości na karcie graficznej, częściowo na procesorze, czy tylko
na procesorze, i proponują mniejszy model, gdy obecny przestaje się mieścić.

[screenshot: Ustawienia > Wydajność]

**Rozwiązywanie problemów:** wolne odpowiedzi na komputerze z kartą graficzną zwykle oznaczają, że model mieści się na karcie
tylko częściowo. Wybierz mniejszy model albo zwiększ **Pamięć graficzną dla AI**. Jeśli komputer przycina podczas pracy, wybierz
Cichy lub obniż **Maksymalne użycie procesora**.

---

## 8. Powiadomienia

**Ustawienia > Powiadomienia** steruje krótkimi komunikatami Windows, które pojawiają się, gdy coś wymaga Twojej uwagi, a okno
jest zamknięte. Są ciche, gdy na pierwszym planie działa program pełnoekranowy, i nie powtarzają się częściej niż raz na kilka
minut.

| Przełącznik | Znaczenie |
|---|---|
| Pobieranie modelu się zakończy lub nie powiedzie | Domyślnie włączone |
| Aplikacja prosi o zgodę | Domyślnie włączone |
| Silnik AI się zatrzyma lub sprawdzenie się nie powiedzie | Domyślnie włączone |
| Urządzenie się sparuje lub połączy z zewnątrz | Domyślnie włączone (alerty z sekcji 10) |

Każdą opcję można wyłączyć osobno. **Przywróć domyślne** na tej samej stronie przywraca je wszystkie.

---

## 9. Udostępnianie tego komputera innym urządzeniom

**Ustawienia > Inne urządzenia.** Domyślnie wyłączone. Po włączeniu Twój laptop albo inny komputer w domowej sieci może poprosić
o użycie AI tego komputera. Urządzenie musi najpierw zostać sparowane kodem, a każda aplikacja na nim nadal wymaga Twojej zgody.

### Parowanie urządzenia

1. Włącz **Pozwól innym urządzeniom korzystać z AI tego komputera**. Windows może poprosić o zezwolenie na regułę sieciową.
2. Naciśnij **Sparuj urządzenie**. Pojawi się kod i kod QR, a **Kopiuj link** skopiuje link do parowania.
3. Na drugim urządzeniu otwórz jego aplikację Portal (albo Portal AI Host w trybie klienta, sekcja 12),
   wybierz połączenie z komputerem, a następnie zeskanuj kod, wpisz go albo wklej link.
4. Urządzenie sprawdza, czy rozmawia z tym komputerem, zanim cokolwiek wyśle, a następnie pokazuje nazwę komputera i odcisk
   do potwierdzenia.

Kod działa jeden raz, przez 5 minut, dla jednego urządzenia. Błędne próby go zużywają; utwórz nowy przyciskiem **Sparuj
urządzenie**. **Anuluj parowanie** kończy proces.

[screenshot: kod parowania i kod QR]

### Sparowane urządzenia

Każde sparowane urządzenie jest wymienione wraz z czasem ostatniego użycia i przyciskiem **Usuń**. Usunięcie urządzenia od razu
kończy jego dostęp, a trwające zapytania się kończą. Aby wrócić, urządzenie musi sparować się ponownie. Można sparować najwyżej
**16** urządzeń.

### Inne ustawienia w tej karcie

- **Dostęp do tego komputera spoza domu** (przekaźnik), zobacz sekcję 13.
  Domyślnie włączone, gdy tylko włączone są inne urządzenia.
- **Klucz dostępu do przekaźnika**, zobacz sekcję 13. Domyślnie wyłączone.
- **Urządzenia mogą zastępować modele**, zobacz sekcję 11. Domyślnie wyłączone.

**Co przechodzi przez sieć:** zapytania i odpowiedzi są przesyłane zaszyfrowane między Twoimi urządzeniami. Połączenie w sieci
domowej używa certyfikatu, który urządzenie zapisało przy parowaniu. Nic nie trafia na nasze serwery, z wyjątkiem opcjonalnego,
„ślepego” przekaźnika (sekcja 13).

**Rozwiązywanie problemów:** *Urządzenie nie może znaleźć tego komputera:* sprawdź, czy oba są w tej samej sieci, czy ten
komputer nie śpi i czy host działa. *Kod jest „nieprawidłowy” albo „wygasł”:* utwórz nowy. *„Nie w sieci domowej”:* najpierw
sparuj urządzenie w domu.

---

## 10. Urządzenia na tym komputerze

Strona **Urządzenia tego komputera** pokazuje jedną kartę na każdy sparowany laptop. To tutaj zobaczysz, co robi każdy laptop,
i możesz go ograniczyć.

[screenshot: Urządzenia tego komputera z dwiema kartami urządzeń]

### Co pokazuje karta

- Stan (Online, Bezczynne, Offline), trasa (Sieć domowa albo Przekaźnik (poza domem)) i czas ostatniej aktywności.
- Co jest uruchomione teraz i co czeka.
- Dzisiejsze zapytania i tokeny z 7-dniowym paskiem, dla każdej aplikacji.
- Używany model, kto go załadował oraz szybkość w tokenach na sekundę i czas do pierwszego bajtu.

### Elementy sterujące

| Element | Co robi |
|---|---|
| Wstrzymaj / Wznów | Wstrzymuje całe urządzenie albo jedną z jego aplikacji. Laptop dostaje wyraźną odpowiedź „wstrzymano” i używa własnego zapasowego rozwiązania, jeśli je ustawiłeś. |
| Zmień nazwę | Zmienia wyświetlaną nazwę urządzenia |
| Limity | Priorytet, dzienne limity, ciche godziny i dozwolone modele (poniżej) |
| Sterowanie modelami (przełącznik) | Pozwala temu urządzeniu wczytywać i wyładowywać modele. Zobacz sekcję 11 |
| Odbierz | Wyładowuje model, który załadowało to urządzenie. Wybór tego komputera wraca. Żadne pliki modeli nie są ruszane. |
| Zapomnij | Usuwa urządzenie. Jego dostęp natychmiast ustaje, a historia użycia zostaje skasowana. Modele nie są ruszane. |

**Limity** (pod przyciskiem **Limity** na karcie, potem **Zapisz**):

- **Taki sam priorytet jak aplikacje tego komputera.** Wyłączone (domyślnie): aplikacje tego komputera idą pierwsze. Włączone:
  urządzenie czeka w tej samej kolejce.
- **Zapytania dziennie** i **Tokeny dziennie.** Domyślnie bez limitu. Gdy limit się wyczerpie, urządzenie dostaje informację,
  by wrócić po północy, czasu tego komputera.
- **Ciche godziny**, z polami **Od** i **Do** (czas tego komputera). Urządzenie nie dostaje odpowiedzi w tych godzinach.
  Domyślnie wyłączone.
- **Modele, których to urządzenie może używać.** Domyślnie wszystkie modele.

### Tryb gry

Na górze strony. **Domyślnie wyłączony.** Po włączeniu zapytania z innych urządzeń dostają informację, że ten komputer jest zajęty,
gdy grasz, aby gry miały kartę graficzną dla siebie. Komputer uznawany jest za „w grze”, gdy Windows zgłasza grę pełnoekranową,
albo gdy inny program obciąża silnik 3D karty graficznej w 40% lub więcej przez około 10 sekund, więc gry w oknie lub w trybie
bez ramek też się liczą. Tryb przestaje działać około 30 sekund po zakończeniu obciążenia. Jeśli liczniki karty graficznej nie
są dostępne na Twoim komputerze, używane jest tylko sprawdzenie pełnego ekranu. Laptop działa wtedy według zapasowego rozwiązania
ustawionego w jego własnych ustawieniach.

### Alerty

Powiadomienie Windows pojawia się, gdy sparuje się nowe urządzenie i gdy urządzenie łączy się przez przekaźnik spoza domu.
Wyłączysz je w **Ustawienia > Powiadomienia > Urządzenie paruje się lub łączy spoza domu**.

### Dane

Dane o użyciu to tylko liczby (zapytania i tokeny dla każdej aplikacji dziennie; przechowywane 7 dni). Tekst zapytań ani odpowiedzi
nie jest zapisywany. **Zapomnij** usuwa plik użycia tego urządzenia i nic więcej.

---

## 11. Sterowanie modelami i dwa modele naraz

Domyślnie sparowany laptop używa tego modelu, jaki ten komputer ma załadowany. Urządzeniu można przyznać więcej.

### Sterowanie modelami

**Ustawienia > Inne urządzenia**, w wierszu urządzenia: **Sterowanie modelami** (domyślnie wyłączone). Po włączeniu laptop może:
- wczytywać i wyładowywać zainstalowane na tym komputerze modele,
- wybierać, gdzie model działa (Automatycznie, Karta graficzna, Pamięć systemowa albo Tylko procesor),
- wybierać, z których modeli tego komputera korzysta każda z jego aplikacji.

Nigdy nie może pobierać, usuwać, sprawdzać ani importować modeli, zmieniać ustawień ani ruszać Twoich plików, z uprawnieniem
albo bez niego. Wyłącz je, a kolejne wywołanie z urządzenia zostanie odrzucone. Usunięcie urządzenia odbiera uprawnienie,
a ponowne parowanie go nie przywraca.

Komputer pokazuje, kto co załadował: „Załadowane przez <urządzenie>: <model> (karta graficzna)”. **Odbierz** (na karcie i w
Ustawieniach) wyładowuje model załadowany przez urządzenie. Własny wybór tego komputera wraca i model załaduje się przy następnym
zapytaniu.

### Dwa modele naraz

Przy włączonym Sterowaniu modelami laptop może utrzymywać na tym komputerze drugi załadowany model, na przykład jeden na karcie
graficznej i jeden w pamięci systemowej.
- Twój własny model zostaje w pierwszym miejscu. Model urządzenia trafia na drugie miejsce, więc urządzenie nigdy nie wypycha
  Twojego modelu, by zrobić miejsce dla swojego.
- Każde miejsce ma własną kolejkę, więc dwa modele mogą odpowiadać jednocześnie. Zapytanie, które nazywa drugi model, idzie do
  niego; wszystko inne idzie do Twojego.
- Przed załadowaniem host liczy, ile zużywa drugie miejsce, względem Twoich limitów. Drugi model, który się nie mieści, jest
  odrzucany („za mało pamięci”). Nic nie jest zatrzymywane po cichu.
- Twoje wybory zawsze mają pierwszeństwo. Jeśli wybierzesz inny model, wyładujesz jeden z nich albo Twój model potrzebuje pamięci,
  model urządzenia zostaje zatrzymany, a urządzenie dostaje informację.
- Drugi model urządzenia nigdy nie zostaje załadowany na zawsze; wyładowuje się jak każdy inny, gdy jest bezczynny.
- **Tylko procesor** uruchamia model bez warstw graficznych, używając około połowy procesora, np. pomocniczy model na procesorze,
  podczas gdy inny działa na karcie graficznej.

[screenshot: strona Modele z dwoma modelami załadowanymi przez różnych właścicieli]

### Urządzenia mogą zastępować modele

**Ustawienia > Inne urządzenia > Urządzenia mogą zastępować modele** (domyślnie wyłączone). Wyłączone: urządzenie nie może
wyładować modelu, którego używają Twoje aplikacje. Włączone: może, a Twoje aplikacje załadują swój model ponownie przy
następnym zapytaniu. Gdy są dwa urządzenia, zachowuje model ten, który załadował go pierwszy.

---

## 12. Korzystanie z domowego komputera z laptopa (tryb klienta)

Tryb klienta sprawia, że Portal AI Host na laptopie korzysta z AI innego Twojego komputera zamiast uruchamiać model samodzielnie.
Aplikacje na laptopie działają jak dotąd.

### Konfiguracja

1. Na domowym komputerze włącz **Pozwól innym urządzeniom korzystać z AI tego komputera** i naciśnij **Sparuj urządzenie**
   (sekcja 9). Naciśnij **Kopiuj link** i wyślij link na laptop.
2. Na laptopie otwórz **Ustawienia > Komputer domowy** (albo wybierz **Użyj mojego komputera domowego** w oknie powitalnym).
3. W **Gdzie działa AI** wybierz **Użyj mojego komputera domowego**. Otworzy się okno **Użyj mojego komputera domowego**. Wklej
   link przyciskiem **Wklej link**, naciśnij **Dalej**, sprawdź nazwę komputera i odcisk, a następnie **Sparuj**. Nazwij laptop.
4. Start pokazuje teraz nazwę komputera i stan: **Połączono — sieć domowa**, **Połączono — przez przekaźnik**,
   **Łączenie...** albo **Brak dostępu**.

[screenshot: okno parowania na laptopie]

### Ustawienia (Ustawienia > Komputer domowy)

| Ustawienie | Wybór | Domyślnie |
|---|---|---|
| Gdzie działa AI | Ten komputer / Użyj mojego komputera domowego | Ten komputer |
| Gdy komputer domowy jest nieosiągalny | **Brak** (aplikacje używają własnego wbudowanego AI) / **Użyj modelu tego laptopa** (ładuje się na żądanie) | Brak |
| Połączenie poza domem | **Automatyczne** / **Ręczne** (wtedy przełącznik **Użyj przekaźnika** dotyczy tylko tej sesji i resetuje się po zamknięciu aplikacji) | Automatyczne |

- Za pierwszym razem, gdy nie można dotrzeć do komputera domowego, zostaniesz raz zapytany, co robić; zmienisz to tutaj w każdej chwili.
- **Sparuj komputer** paruje ponownie; **Odłącz** zapomina komputer (modele i pobrane pliki pozostają bez zmian); **Zmień komputer**
  (na Starcie) otwiera te ustawienia; **Uruchom AI tutaj** (na Starcie) wychodzi z trybu klienta i zachowuje zapisane parowanie.
- Gdy komputer domowy przyznał Sterowanie modelami, pojawia się **Modele na <nazwa komputera>** z przyciskami Wczytaj, Wyładuj
  i umiejscowienia, a także **Model komputera dla każdej aplikacji**. Bez tego strona mówi: „Poproś właściciela <nazwa komputera>
  o włączenie Sterowania modelami dla tego laptopa”.
- Laptop w trybie klienta nie przyjmuje połączeń od innych urządzeń i nie może pełnić roli przekaźnika do Twojego komputera domowego.

### Co dzieje się z modelami samego laptopa

Po wejściu w tryb klienta załadowany model laptopa zostaje wyładowany, by zwolnić pamięć. Pliki modeli, pobierania i folder
modeli pozostają nietknięte. Po wyjściu model ładuje się ponownie, gdy o to poprosisz.

### Rozwiązywanie problemów

- **Brak dostępu:** sprawdź, czy komputer domowy jest włączony i w tej samej sieci, albo włącz przekaźnik.
- **„Ten laptop został usunięty z komputera”:** komputer domowy go usunął. Wybierz **Sparuj ponownie**.
- **„Tożsamość <komputer> się zmieniła”:** certyfikat komputera nie zgadza się już z tym, co zapisał laptop. Jeśli się tego nie
  spodziewałeś, nie parują ponownie. W przeciwnym razie odłącz, a potem sparuj ponownie.
- **Parowanie spoza domu się nie udaje:** najpierw sparuj w domu; link do parowania musi wskazywać adres w Twojej sieci domowej.
- **Wstrzymane, ciche godziny, uruchomiona gra albo wyczerpany dzienny limit:** komputer informuje laptop o powodzie, a laptop używa
  zapasowego rozwiązania.

**Gdzie to jest przechowywane:** laptop trzyma parowanie w folderze danych hosta, w podfolderze `client\`. Klucz urządzenia i tokeny
są chronione dla Twojego użytkownika Windows. **Odłącz** je usuwa. Kopie zapasowe tego folderu nie mogą być przywrócone na innym
koncie użytkownika Windows.

---

## 13. Poza domem: przekaźnik i klucz dostępu do przekaźnika

Przekaźnik pozwala sparowanemu urządzeniu połączyć się z Twoim komputerem z dowolnego miejsca. **Ustawienia > Inne urządzenia >
Dostęp do tego komputera spoza domu.**

- Twój komputer łączy się z małą usługą przekaźnika. Przekaźnik przekazuje tylko zaszyfrowane bajty, których nie może odczytać,
  i nie przechowuje żadnych treści. Każde urządzenie nadal musi być sparowane i podpisuje każde zapytanie.
- Wiersz stanu pokazuje **Dostępny spoza domu**, **Tylko sieć domowa**, **Łączenie z przekaźnikiem...** albo
  **Nie można połączyć się z przekaźnikiem**.
- Działa przez zwykły port webowy, więc większość biurowych sieci na to pozwala.
- **Urządzenia firmowe:** treści służbowe trafią na Twój prywatny komputer. Najpierw sprawdź zasady pracodawcy.

### Klucz dostępu do przekaźnika

**Ustawienia > Inne urządzenia > Klucz dostępu do przekaźnika** (domyślnie wyłączone). Po włączeniu tylko urządzenia sparowane
z kluczem zawartym w kodzie parowania mogą dotrzeć do tego komputera przez przekaźnik, co zapobiega zajmowaniu slotów
przekaźnika przez obcych.

- Włącz go **przed parowaniem**. Urządzenia sparowane wcześniej muszą sparować się ponownie, by korzystać z przekaźnika
  (sieć domowa działa dalej).
- **Nowy klucz** tworzy świeży klucz po potwierdzeniu. Każde sparowane urządzenie musi sparować się ponownie, by korzystać
  z przekaźnika; w domu nic się nie zmienia. Usunięcie urządzenia nie zmienia klucza, więc po usunięciu niezaufanego
  urządzenia użyj **Nowy klucz**.
- Jeśli plik klucza nie może być użyty, przekaźnik pozostaje wyłączony, a nie otwarty, i sieć domowa działa dalej. Uszkodzony plik
  klucza zostaje odłożony pod nazwą z rozszerzeniem `.bad` i nigdy nie jest nadpisywany.
- Jeśli przełącznik nie chce pozostać włączony, usługa przekaźnika może wymagać aktualizacji po swojej stronie. Wyłącz przełącznik.

**Limity:** przekaźnik ogranicza, jak szybko i ile może wysłać urządzenie, aby jedno urządzenie nie przeciążyło usługi. Zwykłe
użycie jest dużo poniżej tych limitów.

---

## 14. Prywatność i raporty o awariach

- **Brak analityki i śledzenia użycia.** Opuścić Twój komputer może tylko raport o awarii i tylko wtedy, gdy go nie wyłączysz.
- **Twój tekst zostaje na Twoich urządzeniach.** Zapytania i odpowiedzi nigdy nie są zapisywane ani rejestrowane przez host.
  Dzienniki zawierają tylko liczby, czasy i stany.
- **Raporty o awariach są domyślnie włączone i możesz je wyłączyć.** Gdy okno albo działający w tle host ulegnie awarii, krótki
  raport pomaga nam ją naprawić. Wyłączysz go w dowolnym momencie w **Ustawienia > Prywatność > Wysyłaj raporty o awariach**;
  działa to od razu. Okno powitalne zawiera ten sam wybór, domyślnie zaznaczony, zanim cokolwiek zostanie wysłane.
  - **Wysyłane:** typ błędu i miejsce, w którym wystąpił (nazwy funkcji i modułów), wersje aplikacji i Windows, informacja, czy
    to kompilacja ze Sklepu, oraz to, czy błąd pochodzi z okna, czy z hosta.
  - **Nigdy nie wysyłane:** zapytania lub odpowiedzi, cokolwiek wpisujesz, nazwy plików i ścieżki folderów, nazwa komputera
    lub użytkownika, które aplikacje używają hosta, ani Twój adres IP. Treść błędu jest odrzucana.
  - **Przechowywane przez 90 dni**, potem usuwane.
  - **Co jest wysyłane** (Ustawienia > Prywatność) otwiera stronę o prywatności.
- **Wszystko jest szyfrowane**, gdy opuszcza ten komputer w kierunku innego Twojego urządzenia.
- **Polityka prywatności:** Informacje > **Polityka prywatności**.
- Pytania lub prośba o usunięcie raportu: mindthaapp@gmail.com.

[screenshot: Ustawienia > Prywatność z przełącznikiem raportów o awariach]

---

## 15. Gdzie przechowywane są dane, kopia zapasowa i usuwanie

Wszystko znajduje się w folderze danych aplikacji. **Informacje > Otwórz folder danych** otwiera go.

| Co | Gdzie (w folderze danych) | Kopia zapasowa? |
|---|---|---|
| Pobrane modele | `models\` albo `Portal AI Host Models\` w folderze wybranym w Ustawieniach | Opcjonalnie. Można je pobrać ponownie. Są duże. |
| Ustawienia | `settings.json` | Tak |
| Dozwolone aplikacje i ich tokeny | Magazyn aplikacji hosta | Nieprzenośne (chronione dla Twojego użytkownika Windows) |
| Sparowane urządzenia, limity, statystyki użycia | Magazyn urządzeń hosta i jeden mały plik statystyk na urządzenie | Nieprzenośne |
| Parowanie laptopa (tryb klienta) | `client\` | Nieprzenośne. Na nowym komputerze sparuj ponownie. |
| Dzienniki | `logs\` (rotacyjne, do 10 MB) | Nie |
| Plik wykrywania dla aplikacji | `discovery\host.json` | Nie |

**Kopia zapasowa:** zamknij Portal AI Host, a następnie skopiuj folder danych. Ustawienia i modele można przywrócić, kopiując je z powrotem.
Uprawnienia aplikacji, parowania urządzeń i tokeny są chronione dla Twojego użytkownika Windows i Twojego komputera, więc na nowym
komputerze trzeba ponownie zezwolić aplikacjom i ponownie sparować urządzenia.

**Usuwanie:**
- *Jeden model:* **Modele > Usuń**. Usuwane są tylko pliki pobrane przez host.
- *Pozostałości:* **Modele > Zwolnij miejsce**.
- *Urządzenie:* **Zapomnij** (Urządzenia tego komputera) albo **Usuń** (Ustawienia).
- *Dostęp aplikacji:* **Usuń dostęp** (Urządzenia i aplikacje).
- *Wszystko:* odinstaluj aplikację. Wybrany przez Ciebie własny folder modeli zostaje na miejscu; jeśli chcesz go usunąć, sam
  skasuj folder `Portal AI Host Models`.

Aplikacja nigdy nie usuwa niczego, czego nie utworzyła, nigdy nie nadpisuje Twoich plików, a model dodany przez Ciebie jest
usuwany tylko jako kopia hosta (Twój oryginalny plik pozostaje nietknięty).

---

## 16. Skróty klawiszowe

| Klawisze | Działanie |
|---|---|
| Ctrl+1 | Start |
| Ctrl+2 | Modele |
| Ctrl+3 | Urządzenia i aplikacje |
| Ctrl+4 | Wypróbuj |
| Ctrl+, | Ustawienia |
| F1 | Informacje |
| Ctrl+Enter | Zapytaj, na stronie Wypróbuj |
| Esc | Zamknij Co nowego albo okno dialogowe |

Lista jest też w menu aplikacji > **Skróty klawiszowe**.

Wygląd: **Ustawienia > Wygląd** oferuje Motyw (domyślnie ustawienie Windows), Materiał okna (Mica), Rogi (Zaokrąglone),
Gęstość (Standardowa), Rozmiar tekstu (Normalny) i Animacje (Zgodnie z Windows). Tryb wysokiego kontrastu Windows zawsze używa
pełnych kolorów systemowych.

---

## 17. Rozwiązywanie problemów i najczęstsze pytania

**Host mówi, że nie działa.** Naciśnij **Uruchom Portal AI Host** na stronie Start. Jeśli nie może wystartować, uruchom ponownie
komputer; jeśli to nie pomoże, zainstaluj aplikację ponownie ze Sklepu.

**„Inny użytkownik Windows na tym komputerze już uruchomił Portal AI Host”.** Poproś tego użytkownika, by zamknął aplikację, albo
zaloguj się jako on.

**Odpowiedzi są wolne.** Zobacz sekcję 7. Model zbyt duży na pamięć graficzną działa częściowo na procesorze.

**Aplikacja mówi, że AI nie jest dostępne.** Sprawdź, czy aplikacja ma dostęp (Urządzenia i aplikacje), czy nie jest
wstrzymana i czy model jest zainstalowany i nie jest za duży dla tego komputera.

**Pierwsza odpowiedź jest wolna, kolejne są szybkie.** Model odpoczywał. Ładuje się przy pierwszym zapytaniu. Wybierz **Zachowaj
model załadowany** w Ustawienia > Ogólne, aby tego uniknąć.

**Czy moje dane gdziekolwiek wychodzą?** Nie. Zapytania i odpowiedzi zostają na Twoich urządzeniach. Opuszczać komputer mogą tylko
raporty o awariach (sekcja 14), a ich wysyłanie możesz wyłączyć.

**Czy mogę korzystać bez internetu?** Tak, gdy model jest pobrany.

**Czy to kosztuje?** Nie.

**Który model wybrać?** Polecany. Wybierz mniejszy, jeśli komputer działa wolno, a większy, jeśli chcesz lepszych odpowiedzi i masz
pamięć.

**Pobieranie utknęło na „Sprawdzanie pobierania”.** Poczekaj. Duże pliki sprawdzają się minutę.

**Parowanie mówi „za dużo urządzeń”.** Limit to 16 urządzeń. Usuń jedno.

**Gdzie jest ikona w obszarze powiadomień?** Kliknij strzałkę przy zegarze, aby pokazać ukryte ikony.

Jeśli nadal masz problem, użyj **Informacje > Kopiuj wersję**, napisz na mindthaapp@gmail.com i wklej wersję do wiadomości.

---

## 18. Planowane funkcje

To są plany i nie ma ich jeszcze w aplikacji.

- **Kategorie modeli na stronie Modele:** pogrupowane według zadań (czat i pisanie, kodowanie, rozumowanie, wizja, osadzenia,
  mowa) z filtrami i wyszukiwaniem.
- **Generowanie obrazów i wideo** na tym komputerze, w tym „reżyser”, który łączy ujęcia w wideo. Modele wideo będą pojawiać się
  jako „Wkrótce”, dopóki silnik nie będzie gotowy.
- **Wybór modelu, który odpowiada, i modele, które proszą się nawzajem o pomoc, w AI Portal.**
- **Wersja na macOS.**

---

## 19. Pomoc techniczna

- **Opinie i pomysły:** Informacje > **Wyślij opinię lub zgłoś pomysł**.
- **Pytania o pomoc techniczną i prywatność:** mindthaapp@gmail.com.
- **Wersja:** Informacje ją pokazują, a **Kopiuj wersję** umieszcza ją w schowku, by wkleić do wiadomości.
- **Licencje:** Portal AI Host zawiera llama.cpp (licencja MIT), która uruchamia modele na Twoim komputerze. Modele są pobierane
  na podstawie własnych licencji (Apache 2.0 lub MIT). Zobacz Informacje.
