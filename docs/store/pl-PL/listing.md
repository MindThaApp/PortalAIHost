# Opis w Sklepie (polski, pl-pl)

Wklej te teksty w Partner Center > zgłoszenie > Store listings > Polski. To tłumaczenie listing-en.md; przed zgłoszeniem
powinien je sprawdzić native speaker. Uwagi do certyfikacji, ocena wiekowa i uzasadnienie uprawnienia runFullTrust pozostają
po angielsku (tylko w listing-en.md).

## Nazwa aplikacji

Portal AI Host

## Krótki opis (do 1000 znaków)

Uruchom lokalny model AI na swoim komputerze i udostępnij go swoim aplikacjom Portal. Jedno kliknięcie wybiera najlepszy
model do Twojego sprzętu, pobiera go i trzyma w gotowości. Twój tekst zostaje na tym komputerze: bez konta, bez chmury,
bez subskrypcji.

## Opis

Portal AI Host daje Twojemu komputerowi własny model AI i pozwala Twoim innym aplikacjom z niego korzystać.

Zamiast tego, by każda aplikacja pobierała własny model albo wysyłała Twój tekst do usługi w chmurze, Portal AI Host trzyma
jeden model na Twoim komputerze i udostępnia go aplikacjom, na które pozwolisz. Voice Portal i inne aplikacje Portal mogą
dzięki temu pisać streszczenia, protokoły i odpowiedzi, używając większego, lepszego modelu, niż mogłyby udźwignąć same.

Prosta konfiguracja
- Przy pierwszym uruchomieniu aplikacja sprawdza Twój komputer (pamięć, kartę graficzną, wolne miejsce na dysku) i poleca
  najlepszy model, który dobrze na nim działa, z jasnym wyjaśnieniem.
- Jedno kliknięcie pobiera model. Pobieranie można wstrzymać i wznowić, a każdy plik jest sprawdzany przed użyciem.
- Nic więcej nie trzeba instalować: silnik jest wbudowany. Bez Pythona, bez wiersza poleceń, bez dodatkowych narzędzi.

Prywatność w założeniach
- Model działa na tym komputerze. Zapytania i odpowiedzi nigdy go nie opuszczają.
- Bez konta i bez logowania.
- Bez analityki. Opcjonalne raporty o awariach nigdy nie zawierają Twoich plików ani tego, co wpisujesz, i możesz je wyłączyć.
  Twoje zapytania nigdy nie są zapisywane ani rejestrowane.
- Host nasłuchuje tylko na tym komputerze (loopback). Inne komputery w Twojej sieci nie mają do niego dostępu.

Ty kontrolujesz
- Aplikacja musi zapytać, zanim będzie mogła użyć hosta, a decyzja należy do Ciebie: zezwól, odmów lub zdecyduj później.
  Dostęp aplikacji możesz odebrać w dowolnym momencie.
- Host pokazuje, czy pytająca aplikacja jest zweryfikowana (jej wydawca), czy nie, abyś wiedział, kto pyta.
- Wybierz, gdzie przechowywane są modele, i usuwaj te, których już nie potrzebujesz. Host usuwa tylko pliki, które sam pobrał.
- Uruchamianie razem z Windows jest opcjonalne i wyłączone, dopóki go nie włączysz.

Działa z Twoim sprzętem
- Korzysta z karty graficznej przez Vulkan (AMD, NVIDIA i Intel) i przechodzi na procesor, gdy nie ma odpowiedniej karty.
- Zwalnia pamięć, gdy model jest bezczynny, albo trzyma go załadowanego, jeśli wolisz.

Dla deweloperów
- Host oferuje standardowe API zgodne z OpenAI pod adresem 127.0.0.1 (uzupełnianie czatu ze strumieniowaniem, osadzenia
  i lista modeli), więc istniejące biblioteki klienckie działają. Każda aplikacja najpierw potrzebuje zgody użytkownika.

Modele są pobierane ze stron ich wydawców na Hugging Face. Każdy model ma własną licencję; aplikacja oferuje tylko modele,
których licencje pozwalają na użytek komercyjny (Apache 2.0 lub MIT).

## Funkcje (Partner Center „Product features”, maks. 20, po 200 znaków)

1. Uruchamia model AI lokalnie na Twoim komputerze, bez chmury
2. Jeden model dla wszystkich Twoich aplikacji Portal
3. Poleca najlepszy model do Twojego sprzętu
4. Pobieranie jednym kliknięciem, z wstrzymywaniem, wznawianiem i sprawdzaniem plików
5. Korzysta z karty graficznej (Vulkan) z automatycznym przejściem na procesor
6. Bez konta, bez logowania, bez subskrypcji
7. Bez analityki; raporty o awariach są opcjonalne i oczyszczone; zapytania nigdy nie są zapisywane ani rejestrowane
8. Nasłuch tylko na tym komputerze (loopback)
9. Aplikacje muszą zapytać; Ty zezwalasz, odmawiasz lub odbierasz dostęp
10. Pokazuje, czy pytająca aplikacja jest zweryfikowana
11. Zwalnia pamięć, gdy jest bezczynny, albo trzyma model załadowany
12. Wybór folderu dla modeli
13. Opcjonalny start razem z Windows, z ikoną w obszarze powiadomień
14. Pole „Wypróbuj”, by sprawdzić, czy model działa
15. Lokalne API zgodne z OpenAI dla deweloperów
16. Uczciwy podział, gdy kilka aplikacji pyta naraz
17. Samo się odzyskuje, gdy silnik się zatrzyma

## Słowa kluczowe (7)

1. lokalne AI
2. AI offline
3. prywatne AI
4. LLM
5. model AI
6. model językowy
7. serwer AI

Bez nazw produktów innych firm w słowach kluczowych (zasada Store 10.1.3).

## Co nowego w tej wersji (1.0)

Pierwsze wydanie. Pobierz lokalny model AI jednym kliknięciem i udostępnij go swoim aplikacjom Portal, prywatnie, na swoim
komputerze.

## Wymagania systemowe (tekst na liście)

Minimum (ustawiane w Partner Center > Właściwości > Wymagania systemowe):
- Windows 11 (wersja 21H2, kompilacja 22000) lub nowszy, x64 lub Arm64.
- Pamięć: 8 GB.
- Wolne miejsce na dysku: 5 GB (sama aplikacja jest mała; modele zajmują od 2 do 20 GB, większe opcjonalne modele do około
  140 GB).

Zalecane:
- Pamięć: 16 GB lub więcej (32 GB lub więcej dla dużych modeli).
- Grafika: karta graficzna z obsługą Vulkan i 8 GB pamięci wideo lub więcej (AMD, NVIDIA lub Intel).
- Wolne miejsce na dysku: 25 GB.

Dodatkowe wymagania systemowe (tekst pod opisem):
> Najlepiej działa z 16 GB pamięci i kartą graficzną z 8 GB pamięci wideo. Bez odpowiedniej karty graficznej model działa
> na procesorze, co jest wolniejsze. Komputery z Arm64 uruchamiają model na procesorze. Modele wymagają od 2 do 20 GB wolnego
> miejsca na dysku.
