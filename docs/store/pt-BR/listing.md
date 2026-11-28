# Ficha da Store (português do Brasil, pt-BR)

Tradução da ficha da Store em inglês (listing-en.md). Somente as seções visíveis ao usuário. Nomes de produtos e links não mudam.

## Nome do aplicativo

Portal AI Host

## Descrição curta

Execute um modelo de IA local no seu próprio PC e compartilhe-o com seus aplicativos Portal. Com um clique, o melhor modelo para o seu hardware é escolhido, baixado e deixado pronto. Seu texto fica neste PC: sem conta, sem nuvem, sem assinatura.

## Descrição

O Portal AI Host dá ao seu PC um modelo de IA próprio e permite que seus outros aplicativos o utilizem.

Em vez de cada aplicativo baixar o próprio modelo ou enviar seu texto a um serviço na nuvem, o Portal AI Host mantém um único modelo no seu PC e o compartilha com os aplicativos que você autorizar. Assim, o Voice Portal e os demais aplicativos Portal podem escrever resumos, atas e respostas com um modelo maior e melhor do que conseguiriam carregar sozinhos.

Fácil de configurar
- Na primeira execução, o aplicativo verifica seu PC (memória, placa de vídeo, espaço livre em disco) e recomenda o melhor modelo que roda bem nele, com uma explicação clara.
- Um clique o baixa. Os downloads podem ser pausados e retomados, e cada arquivo é verificado antes de ser usado.
- Nada mais para instalar: o mecanismo já vem incluído. Sem Python, sem linha de comando, sem ferramentas extras.

Privado por design
- O modelo roda neste PC. Os prompts e as respostas nunca saem dele.
- Sem conta e sem login.
- Sem análises de uso. Os relatórios de falha opcionais nunca incluem seus arquivos nem o que você digita, e podem ser desativados. Seus prompts nunca são salvos nem registrados.
- O host escuta apenas neste PC (loopback). Outros computadores da sua rede não conseguem acessá-lo.

Você mantém o controle
- Um aplicativo precisa pedir permissão antes de usar o host, e você decide: permitir, negar ou decidir depois. Você pode remover o acesso de um aplicativo a qualquer momento.
- O host mostra se um aplicativo é verificado (o seu editor), para que você saiba quem está pedindo.
- Escolha onde os modelos são armazenados e remova os que não precisar mais. O host só apaga arquivos que ele mesmo baixou.
- Iniciar com o Windows é opcional e fica desativado até você ativar.

Funciona com o seu hardware
- Usa a sua placa de vídeo por meio do Vulkan (AMD, NVIDIA e Intel) e recorre ao processador quando não há uma GPU adequada.
- Libera a memória quando o modelo fica ocioso, ou mantém o modelo carregado, se preferir.

Para desenvolvedores
- O host oferece uma API padrão compatível com a OpenAI em 127.0.0.1 (chat completions com streaming, embeddings e lista de modelos), então as bibliotecas cliente existentes funcionam. Cada aplicativo precisa da aprovação do usuário primeiro.

Os modelos são baixados das páginas dos seus editores no Hugging Face. Cada modelo tem sua própria licença; o aplicativo oferece apenas modelos cujas licenças permitem uso comercial (Apache 2.0 ou MIT).

## Recursos (Partner Center, "Recursos do produto", máximo de 20, 200 caracteres cada)

1. Executa um modelo de IA localmente no seu PC, sem precisar de nuvem
2. Um único modelo compartilhado por todos os seus aplicativos Portal
3. Recomenda o melhor modelo para o seu hardware
4. Download com um clique, com pausa, retomada e verificação de arquivos
5. Usa a sua placa de vídeo (Vulkan), com alternativa automática no processador
6. Sem conta, sem login, sem assinatura
7. Sem análises de uso; relatórios de falha são opcionais e higienizados; prompts nunca são salvos nem registrados
8. Escuta apenas neste PC (loopback)
9. Os aplicativos precisam pedir permissão; você permite, nega ou remove o acesso
10. Mostra se um aplicativo solicitante é verificado
11. Libera a memória quando ocioso, ou mantém o modelo carregado
12. Escolha a pasta para os seus modelos
13. Início opcional com o Windows, com ícone na bandeja
14. Caixa "Experimente" para verificar se o modelo funciona
15. API local compatível com a OpenAI para desenvolvedores
16. Divisão justa quando vários aplicativos pedem ao mesmo tempo
17. Recupera-se sozinho se o mecanismo parar

## Termos de pesquisa (palavras-chave, 7)

1. IA local
2. IA offline
3. IA privada
4. LLM
5. modelo de IA
6. modelo de linguagem
7. servidor de IA

Nenhum nome de produto de terceiros nas palavras-chave (política da Store 10.1.3).

## Novidades desta versão (1.0)

Primeira versão. Baixe um modelo de IA local com um clique e compartilhe-o de forma privada com seus aplicativos Portal, no seu próprio PC.

## Requisitos do sistema (texto da ficha)

> Funciona melhor com 16 GB de memória e uma placa de vídeo com 8 GB de memória de vídeo. Sem uma placa de vídeo adequada, o modelo roda no processador, o que é mais lento. PCs Arm64 executam o modelo no processador. Os modelos precisam de 2 a 20 GB de espaço livre em disco.

Mínimo: Windows 11 (versão 21H2, build 22000) ou posterior, x64 ou Arm64. Memória: 8 GB. Espaço livre em disco: 5 GB (o aplicativo em si é pequeno; os modelos têm de 2 a 20 GB, e os modelos opcionais maiores chegam a cerca de 140 GB).

Recomendado: 16 GB de memória ou mais (32 GB ou mais para os modelos grandes). Uma GPU compatível com Vulkan e 8 GB de memória de vídeo ou mais (AMD, NVIDIA ou Intel). 25 GB de espaço livre em disco.
