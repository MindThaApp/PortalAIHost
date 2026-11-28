# Portal AI Host: guia do usuário

O Portal AI Host executa um modelo de IA no seu PC e o compartilha com seus aplicativos Portal. Suas perguntas e seus arquivos ficam nos seus próprios equipamentos. Este guia explica cada parte do aplicativo, o que fazem as suas configurações e o que fazer quando algo dá errado.

Os lugares onde deve entrar uma imagem estão marcados como `[screenshot: ...]`.

## Sumário

1. Início rápido
2. Requisitos do sistema, instalação, atualização e desinstalação
3. Conhecendo o aplicativo
4. Modelos
5. Aplicativos e permissões
6. Experimente
7. Configurações de desempenho
8. Notificações
9. Permitir que outros dispositivos usem este PC
10. Dispositivos neste PC
11. Controle de modelos e dois modelos ao mesmo tempo
12. Usar o seu PC de casa a partir de um notebook (modo cliente)
13. Fora de casa: o relay e a chave de acesso ao relay
14. Privacidade e relatórios de falha
15. Onde os seus dados ficam guardados, backup e remoção
16. Atalhos de teclado
17. Solução de problemas e perguntas frequentes
18. Próximos passos
19. Suporte

---

## 1. Início rápido

1. Instale o Portal AI Host na Microsoft Store e abra o aplicativo.
2. A janela de boas-vindas verifica o seu PC (memória, placa de vídeo, espaço livre em disco) e recomenda um modelo. Escolha **Começar com as configurações recomendadas**, ou **Baixar** para escolher o modelo por conta própria. **Escolher depois** pula essa etapa.
3. Procure o ícone perto do relógio. O Portal AI Host fica ali em silêncio. Clique nele para ver um resumo rápido.
4. Abra **Experimente** e faça uma pergunta curta para confirmar que o modelo funciona.
5. Na primeira vez que um aplicativo Portal quiser usar IA, uma janela perguntará se você quer permitir ou negar.

[screenshot: a janela de boas-vindas com o modelo recomendado]

Se, em vez disso, você quer que um notebook use a IA do seu PC de casa, escolha **Usar meu PC de casa** na janela de boas-vindas. Veja a seção 12.

**Gratuito ou pago:** o Portal AI Host é gratuito. Nenhum recurso deste guia exige conta, login ou compra.

---

## 2. Requisitos do sistema, instalação, atualização e desinstalação

### Requisitos

| | Mínimo | Recomendado |
|---|---|---|
| Windows | Windows 11 (21H2, build 22000) ou posterior, x64 ou Arm64 | O mesmo |
| Memória | 8 GB | 16 GB ou mais (32 GB ou mais para os modelos grandes) |
| Gráficos | Nenhuma (o modelo roda então no processador, que é mais lento) | Uma placa de vídeo compatível com Vulkan e 8 GB de memória de vídeo ou mais (AMD, NVIDIA ou Intel) |
| Espaço livre em disco | 5 GB | 25 GB |

Os modelos têm de 2 a 20 GB cada. PCs Arm64 executam o modelo no processador. A conexão com a internet só é necessária para baixar modelos e para o relay; depois que um modelo está no seu PC, ele funciona offline.

### Instalação

Instale pela Microsoft Store. Não é preciso mais nada: nada de Python, linha de comando ou outro software. O mecanismo de IA faz parte do aplicativo.

### Iniciar com o Windows

**Configurações > Geral > Iniciar com o Windows** (padrão: desativado, exceto se você escolher "Começar com as configurações recomendadas" na janela de boas-vindas, que o ativa). Quando ativado, o Portal AI Host espera na bandeja depois que você entra no Windows, para que os aplicativos possam usá-lo imediatamente.
- Se a chave disser que só está disponível no aplicativo instalado, você está executando uma cópia de desenvolvimento.
- Se você a desativou no Gerenciador de Tarefas (aplicativos de inicialização), reative-a por lá.
- Se a sua organização definir essa opção, a chave fica bloqueada.

### Atualização

A Store atualiza o aplicativo. Depois de uma atualização, **Novidades** abre uma vez. Você pode abri-la novamente pelo menu do aplicativo. Os modelos permanecem no lugar entre as atualizações.

### Desinstalação

Remova o aplicativo em **Configurações do Windows > Aplicativos**. Os modelos da pasta padrão ficam guardados dentro da pasta de dados do aplicativo e são removidos junto com ele. Uma pasta de modelos personalizada que você escolheu não é alterada. Veja a seção 15 antes de desinstalar se quiser manter algo.

---

## 3. Conhecendo o aplicativo

O lado esquerdo tem cinco páginas.

| Página | Para que serve | Atalho |
|---|---|---|
| Início | Estado, modelo ativo, uso de memória, o aplicativo que está usando a IA agora | Ctrl+1 |
| Modelos | Baixar, usar e remover modelos | Ctrl+2 |
| Dispositivos e aplicativos | Permitir ou remover aplicativos Portal, pausá-los, definir prioridade | Ctrl+3 |
| Experimente | Fazer ao modelo uma pergunta curta | Ctrl+4 |
| Configurações | Tudo o que pode ser configurado | Ctrl+, |

Uma sexta página, **Dispositivos deste PC**, lista os notebooks e celulares pareados com este PC (veja a seção 10). O menu do aplicativo contém Configurações, Atalhos de teclado, Novidades e Sobre.

**Início** mostra se a IA está **Pronta**, **Respondendo**, **Preparando o modelo**, **Em repouso** (o modelo acorda quando um aplicativo pede) ou se **ainda não tem modelo**. **AGORA** mostra qual aplicativo está recebendo uma resposta e aproximadamente quantas palavras por segundo. Também mostra quanta memória e memória de vídeo o modelo usa, além de um botão **Carregar agora** / **Descarregar agora**. **HOJE** conta as solicitações de cada aplicativo. Um cartão de saúde mostra "Tudo certo" ou lista o que precisa de atenção, cada item com uma correção de um clique, como Carregar o modelo, Reiniciar o mecanismo, Verificar arquivos ou Abrir configurações de desempenho.

[screenshot: a página Início enquanto um aplicativo usa o modelo]

O ícone da bandeja abre uma pequena janela: modelo, memória, última solicitação e fila, com os botões **Abrir aplicativo** e **Descarregar modelo**.

[screenshot: a janela da bandeja]

Se o programa host não estiver em execução, o Início oferece **Iniciar o Portal AI Host**. Se ele fechou inesperadamente mais de uma vez, uma barra de **Modo de segurança** indica que foi iniciado com configurações cautelosas. Seus aplicativos continuam funcionando; reinicie o PC se isso acontecer de novo.

---

## 4. Modelos

Um modelo é a IA que faz o trabalho. **Modelos** (Ctrl+2) lista o que você tem e o que pode adicionar.

### Modelo recomendado

A primeira execução e a página Início recomendam "o melhor modelo para este PC", com um motivo claro, com base na sua memória, na memória de vídeo e no espaço livre em disco. Quando um modelo melhor fica disponível, uma barra mostra **Há um modelo melhor disponível**. O modelo atual continua até que o novo esteja pronto, e você decide quando remover o antigo.

### Baixar um modelo

1. Abra **Modelos**. Escolha um modelo em **Modelos que você pode adicionar**.
2. Pressione **Baixar**. Uma barra mostra o progresso e o tempo restante.
3. **Pausar** e **Retomar** estão disponíveis a qualquer momento. **Parar** descarta a parte já baixada (após confirmação).
4. O host verifica os arquivos baixados antes de usá-los.
5. Quando terminar, pressione **Usar este modelo**.

Os downloads podem ser retomados depois de reiniciar e são verificados arquivo por arquivo. Um download precisa de espaço livre suficiente; se não houver, a página mostra quanto falta e oferece **Alterar pasta nas Configurações**. O PC permanece acordado enquanto um download está em andamento (Configurações > Desempenho, ativado por padrão).

[screenshot: a página Modelos com um download em andamento]

Um modelo marcado como "Este modelo pode ser grande demais para este PC" ainda pode ser baixado, mas pode rodar devagar ou não caber.

### Detalhes, verificar arquivos, baixar novamente, remover

- **Detalhes** mostra para que o modelo serve, o que observar, a memória que ele precisa, sua licença, quando foi usado pela última vez e quanto espaço ocupa.
- **Verificar arquivos** confere um modelo no disco de novo. Nunca apaga nada. Um modelo danificado é marcado como **Danificado**.
- **Baixar novamente** (para um modelo danificado) remove a cópia danificada e baixa outra. Nada mais é apagado.
- **Remover** tira um modelo deste PC após uma confirmação que informa quanto espaço será liberado. Se o modelo estiver em uso, o host primeiro o descarrega e pergunta o que usar em seguida. Somente os arquivos baixados pelo host são apagados.

### Adicionar o seu próprio modelo

**Adicionar seu próprio modelo...** permite escolher um arquivo `.gguf`. O host cria a própria cópia na pasta de modelos. O original nunca é alterado nem movido. A linha fica marcada como **Adicionado por você**, e o host não consegue estimar a memória necessária, então teste e veja. **Remover** em um modelo assim apaga apenas a cópia do host.

### Liberar espaço

**Liberar espaço** lista os restos criados pelo próprio aplicativo: downloads incompletos que não podem ser retomados, cópias antigas que ficaram depois de mover seus modelos e modelos que uma verificação encontrou danificados. Você confirma antes de qualquer remoção. Os modelos que você usa e os arquivos que você mesmo adicionou nunca aparecem na lista. Downloads incompletos que ainda podem ser retomados são mantidos por uma semana.

### Pasta dos modelos e como mover os seus modelos

**Configurações > Pasta dos modelos** define para onde vão os novos downloads. Deixe em branco para usar a pasta padrão, ou digite um caminho completo, como `D:\Modelos`, e pressione Enter. Modelos já baixados não são movidos nem apagados: continuam onde estão e seguem listados. Para movê-los, aceite **Mover meus modelos para lá**. Eles são copiados, verificados e só depois as cópias antigas são removidas. O modelo em uso é descarregado durante a operação, e **Cancelar movimentação** deixa tudo onde estava.

### Orçamento de memória e barras

As barras **Orçamento de memória** na página Modelos mostram RAM e memória de vídeo: quanto os modelos carregados usam, quanto fica reservado enquanto um modelo carrega e quanto resta livre em relação aos seus limites (veja a seção 7). Um carregamento que ultrapasse o seu limite de memória é recusado, com um motivo claro, antes que qualquer coisa seja interrompida.

[screenshot: as barras de Orçamento de memória com dois modelos carregados]

### Onde o modelo roda: placa de vídeo, memória ou só processador

Onde um modelo é executado é definido em **Configurações > Desempenho > Executar em** (Automático, Placa de vídeo, Processador). Um modelo que não cabe na placa de vídeo roda parcialmente no processador (mais devagar). Veja também a escolha por modelo na seção 11, que inclui **Somente processador**.

### Limites e gratuito ou pago

- Todos os modelos da lista são gratuitos para uso comercial (licenças Apache 2.0 ou MIT); cada modelo mostra a sua própria licença.
- Os modelos são baixados das páginas dos seus editores no Hugging Face. O host aceita apenas uma lista de modelos confiável e assinada. Se não conseguir carregá-la, uma barra mostra **Lista de modelos indisponível**; os modelos que já estão neste PC continuam funcionando.

### Solução de problemas

- *Nenhum modelo aparece para baixar:* verifique sua conexão com a internet e pressione **Tentar novamente** no Início.
- *O download para:* pressione **Retomar**; se faltar espaço livre, libere espaço ou altere a pasta.
- *Modelo "pode ser grande demais":* escolha um menor ou reduza a carga em Configurações > Desempenho.

---

## 5. Aplicativos e permissões

Abra **Dispositivos e aplicativos** (Ctrl+3).

- **Aguardando sua resposta:** um aplicativo que pediu para usar a IA aparece aqui e também como aviso ("Permitir que o Voice Portal use a IA neste PC?"). Escolha **Permitir**, **Não permitir** ou **Decidir depois**. O aviso mostra se o aplicativo é um aplicativo Portal verificado, um aplicativo instalado que não é do Portal, ou não verificado (nesse caso informa de onde ele foi iniciado; permita-o somente se você mesmo o iniciou).
- **Aplicativos que podem usar o seu host de IA:** cada aplicativo permitido mostra as solicitações de hoje e quando foi usado pela última vez.
  - **Pausar** recusa novas solicitações até você retomar; o aplicativo mantém o acesso.
  - **Prioridade** (Alta, Normal, Baixa) define quem vai primeiro quando vários aplicativos pedem ao mesmo tempo. Solicitações interativas passam na frente do trabalho em lote, e uma solicitação que espera muito sobe de posição para que um aplicativo de baixa prioridade nunca fique sem atendimento.
  - **Remover acesso** interrompe o aplicativo até você permiti-lo novamente. **Desfazer** aparece por um momento.

Os aplicativos precisam pedir antes de poder usar o host. O host escuta apenas neste PC, a menos que você ative o compartilhamento com outros dispositivos (seção 9).

[screenshot: a página Dispositivos e aplicativos com um aplicativo aguardando e um aplicativo permitido]

Para desenvolvedores: o host oferece uma API local compatível com a OpenAI em `http://127.0.0.1:41870` (chat completions com streaming, embeddings, lista de modelos). Veja `api-v1.md`.

**Onde fica guardado:** os aplicativos permitidos e os seus tokens ficam na pasta de dados do host, protegida para o seu usuário do Windows. Remover o acesso de um aplicativo remove o seu token.

---

## 6. Experimente

**Experimente** (Ctrl+4) verifica se o modelo funciona. Digite uma pergunta curta e pressione **Perguntar** (ou Ctrl+Enter). **Parar** encerra a resposta, **Limpar** esvazia a página. Perguntas sugeridas são oferecidas. A página mostra quanto tempo levaram as primeiras palavras e a velocidade em tokens por segundo. Nada do que você digita aqui é salvo.

[screenshot: Experimente com uma resposta e a linha de tempo]

---

## 7. Configurações de desempenho

**Configurações > Desempenho** evita que a IA deixe o seu PC lento. As alterações são aplicadas na hora; um modelo que deixa de caber é recarregado com os novos limites ou descarregado (continua no disco). **Restaurar padrões** devolve a seção ao estado original.

| Configuração | O que faz | Padrão |
|---|---|---|
| Desempenho | Uma predefinição: **Silencioso** (cerca de metade da velocidade, o PC continua responsivo), **Equilibrado** (boa velocidade, seus programas têm prioridade), **Rápido** (respostas mais rápidas, outros programas podem ficar mais lentos), **Personalizado** | Equilibrado |
| Memória máxima para a IA | A IA nunca usa mais memória do que isso | 80% da memória do PC |
| Sempre deixar livre | Memória mantida livre para os seus outros programas | Definido pela predefinição |
| Executar em | **Automático** (placa de vídeo quando ajuda), **Placa de vídeo** (o mais rápido, se o modelo couber), **Processador** (mais lento, deixa a placa de vídeo livre para jogos) | Automático |
| Memória de vídeo para a IA | Um limite da memória de vídeo que a IA pode usar | Definido pela predefinição |
| Núcleos do processador | Menos núcleos mantêm o PC responsivo | Definido pela predefinição |
| Prioridade | **Baixa** (seu trabalho primeiro) ou **Normal** | Baixa |
| Uso máximo do processador | Um teto para o uso do processador | Sem limite (100%) |
| Reduzir atividade quando | A IA roda com a menor prioridade, usa no máximo metade do seu processador e volta ao normal quando termina. Você pode escolher qualquer uma destas opções: um jogo ou programa em tela cheia está rodando, o PC está na bateria, um aplicativo Portal está ocupado com trabalho ao vivo, como ditado | Programa em tela cheia, bateria e aplicativo ocupado ativados |

Em **Liberar memória quando ocioso**, dentro de **Configurações > Geral**, você escolhe **Manter o modelo carregado**, **Após 5 minutos** ou **Após 10 minutos** (padrão de 10 minutos, 5 na bateria). O modelo volta a carregar na próxima vez que um aplicativo pedir. Mantê-lo carregado responde mais rápido, mas usa memória o tempo todo.

As dicas dos controles deslizantes informam se a memória escolhida faz um modelo caber inteiro na placa de vídeo, parcialmente no processador ou somente no processador, e sugerem um modelo menor quando o atual deixa de caber.

[screenshot: Configurações > Desempenho]

**Solução de problemas:** respostas lentas em um PC com placa de vídeo normalmente significam que o modelo só cabe parcialmente na placa. Escolha um modelo menor ou aumente a Memória de vídeo para a IA. Se o PC travar durante o uso, escolha Silencioso ou reduza o Uso máximo do processador.

---

## 8. Notificações

**Configurações > Notificações** controla as mensagens curtas do Windows exibidas quando algo precisa da sua atenção e a janela está fechada. Elas ficam em silêncio enquanto um programa em tela cheia está em primeiro plano, e nunca se repetem mais de uma vez a cada poucos minutos.

| Chave | Significado |
|---|---|
| Um download de modelo termina ou falha | Ativado por padrão |
| Um aplicativo pede aprovação | Ativado por padrão |
| O mecanismo de IA para ou uma verificação falha | Ativado por padrão |
| Um dispositivo é pareado ou se conecta de fora de casa | Ativado por padrão (os alertas da seção 10) |

Cada uma pode ser desativada separadamente. **Restaurar padrões** na mesma página as restaura.

---

## 9. Permitir que outros dispositivos usem este PC

**Configurações > Outros dispositivos.** Desativado por padrão. Quando ativado, o seu notebook ou outro PC da sua rede doméstica pode pedir para usar a IA deste PC. Um dispositivo precisa ser pareado com um código antes, e cada aplicativo nele ainda precisa da sua permissão.

### Parear um dispositivo

1. Ative **Permitir que outros dispositivos usem a IA deste PC**. O Windows pode pedir para permitir uma regra de rede.
2. Pressione **Parear um dispositivo**. Aparecem um código e um QR code, e **Copiar link** copia um link de pareamento.
3. No outro dispositivo, abra o aplicativo Portal dele (ou o Portal AI Host no modo cliente, seção 12), escolha se conectar a um PC e, então, escaneie o código, digite-o ou cole o link.
4. Ele confere se está falando com este PC antes de enviar qualquer coisa, e então mostra o nome do PC e uma impressão digital para confirmação.

O código funciona uma vez, por 5 minutos, para um único dispositivo. Tentativas erradas o gastam; gere um novo com **Parear um dispositivo**. **Cancelar pareamento** o encerra.

[screenshot: o código de pareamento e o QR code]

### Dispositivos pareados

Cada dispositivo pareado aparece com a hora do último uso e um botão **Remover**. Remover um dispositivo corta o acesso dele imediatamente, e as solicitações em andamento terminam. Para voltar, ele precisa parear de novo. No máximo **16** dispositivos podem ser pareados.

### Outras configurações neste cartão

- **Acessar também este PC de fora de casa** (o relay), veja a seção 13. Padrão: ativado assim que os outros dispositivos estiverem habilitados.
- **Chave de acesso ao relay**, veja a seção 13. Padrão: desativado.
- **Dispositivos podem substituir modelos**, veja a seção 11. Padrão: desativado.

**O que trafega pela rede:** solicitações e respostas viajam criptografadas entre os seus próprios dispositivos. A conexão da rede doméstica usa um certificado que o dispositivo fixou ao parear. Nada vai para um servidor nosso, exceto, opcionalmente, o relay cego (seção 13).

**Solução de problemas:** *Um dispositivo não encontra este PC:* confira se os dois estão na mesma rede, se este PC está acordado e se o host está em execução. *O código está "incorreto" ou "expirou":* gere outro. *"Não está na sua rede doméstica":* pareie uma vez em casa primeiro.

---

## 10. Dispositivos neste PC

A página **Dispositivos deste PC** mostra um cartão por notebook pareado. É o lugar para ver o que cada notebook está fazendo e limitá-lo.

[screenshot: Dispositivos deste PC com dois cartões de dispositivos]

### O que um cartão mostra

- Estado (Online, Ocioso, Offline), rota (Rede doméstica ou Relay (fora de casa)) e visto pela última vez.
- O que está rodando agora e o que está esperando.
- Solicitações e tokens de hoje, com uma barra de 7 dias, por aplicativo.
- O modelo em uso, quem o carregou, e a velocidade em tokens por segundo e o tempo até o primeiro byte.

### Controles

| Controle | O que faz |
|---|---|
| Pausar / Retomar | Pausa o dispositivo inteiro, ou um dos seus aplicativos. O notebook recebe uma resposta clara de "pausado" e usa o próprio plano alternativo, se você tiver definido um. |
| Renomear | Altera o nome exibido do dispositivo |
| Limites | Prioridade, limites diários, horário de silêncio e modelos permitidos (abaixo) |
| Controle de modelos (chave) | Permite que este dispositivo carregue e descarregue modelos. Veja a seção 11 |
| Retomar controle | Descarrega o modelo que este dispositivo carregou. A escolha própria deste PC volta. Nenhum arquivo de modelo é tocado. |
| Esquecer | Remove o dispositivo. O acesso dele para imediatamente e o histórico de uso é apagado. Os modelos não são afetados. |

**Limites** (sob o botão **Limites** do cartão, depois **Salvar**):

- **Mesma prioridade dos aplicativos deste PC.** Desativado (padrão): os aplicativos deste PC vão primeiro. Ativado: o dispositivo espera na mesma fila.
- **Solicitações por dia** e **Tokens por dia.** Padrão: sem limite. Quando esgotados, o dispositivo é avisado para voltar depois da meia-noite (horário deste PC).
- **Horário de silêncio**, com **De** e **Até** (horário deste PC). O dispositivo não recebe respostas nesses horários. Padrão: desativado.
- **Modelos que este dispositivo pode usar.** Padrão: todos os modelos.

### Modo jogo

No topo da página. **Padrão: desativado.** Quando ativado, as solicitações de outros dispositivos recebem a resposta de que este PC está ocupado enquanto você joga, para que os jogos mantenham a placa de vídeo. Este PC conta como "jogando" quando o Windows informa um jogo em tela cheia, ou quando outro programa mantém o mecanismo 3D da placa de vídeo com 40% ou mais de uso por cerca de 10 segundos; assim, jogos em janela ou em janela sem bordas também contam. Ele deixa de contar cerca de 30 segundos depois que a carga termina. Se os contadores gráficos não estiverem disponíveis no seu PC, só a verificação de tela cheia é usada. O notebook então recorre às próprias configurações.

### Alertas

Uma notificação do Windows aparece quando um novo dispositivo é pareado e quando um dispositivo se conecta pelo relay de fora de casa. Desative-as em **Configurações > Notificações > Um dispositivo é pareado ou se conecta de fora de casa**.

### Dados

Os números de uso são apenas contagens (solicitações e tokens por aplicativo e por dia, 7 dias guardados). Nenhum texto de prompt ou de resposta é armazenado. **Esquecer** apaga o arquivo de uso desse dispositivo e nada mais.

---

## 11. Controle de modelos e dois modelos ao mesmo tempo

Por padrão, um notebook pareado usa o modelo que este PC tiver carregado. Você pode dar mais poder a um dispositivo.

### Controle de modelos

**Configurações > Outros dispositivos**, na linha do dispositivo: **Controle de modelos** (padrão: desativado). Com ele ativado, o notebook pode:
- carregar e descarregar modelos instalados neste PC,
- escolher onde um modelo roda (Automático, Placa de vídeo, Memória do sistema ou Somente processador),
- escolher quais modelos deste PC cada um dos seus aplicativos usa.

Ele nunca pode baixar, remover, verificar ou importar modelos, alterar configurações ou mexer nos seus arquivos, com ou sem essa permissão. Se você desativar, a próxima chamada do dispositivo é recusada. Remover o dispositivo elimina a permissão, e parear novamente não a restaura.

O PC mostra quem carregou o quê: "Carregado por <dispositivo>: <modelo> (placa de vídeo)". **Retomar controle** (no cartão e nas Configurações) descarrega o modelo que um dispositivo carregou. A escolha própria deste PC volta e é carregada na próxima solicitação.

### Dois modelos ao mesmo tempo

Com o Controle de modelos, um notebook pode manter um segundo modelo carregado neste PC, por exemplo um na placa de vídeo e outro na memória do sistema.
- O seu modelo fica no primeiro slot. O modelo de um dispositivo vai para o segundo, então um dispositivo nunca substitui o seu modelo só para conseguir o dele.
- Cada slot tem a sua própria fila, então dois modelos podem responder ao mesmo tempo. Uma solicitação que nomeia o segundo modelo vai para ele; todo o resto vai para o seu.
- Antes de carregar, o host soma o que o outro slot já usa em relação aos seus limites. Um segundo modelo que não cabe é recusado ("memória insuficiente"). Nada é interrompido em silêncio.
- As suas escolhas sempre têm prioridade. Se você escolher outro modelo, descarregar um, ou se o seu modelo precisar da memória, o modelo do dispositivo é parado e o dispositivo é avisado.
- O segundo modelo de um dispositivo nunca fica carregado para sempre; ele é descarregado como qualquer outro quando está ocioso.
- **Somente processador** executa um modelo sem camadas gráficas, usando cerca de metade do processador; por exemplo, um modelo auxiliar no processador enquanto outro usa a placa de vídeo.

[screenshot: a página Modelos com dois modelos carregados por donos diferentes]

### Dispositivos podem substituir modelos

**Configurações > Outros dispositivos > Dispositivos podem substituir modelos** (padrão: desativado). Desativado: um dispositivo não pode descarregar um modelo que os seus aplicativos estejam usando. Ativado: pode, e os seus aplicativos carregam o modelo deles novamente na próxima solicitação. Com dois dispositivos, o primeiro que carregou mantém o seu modelo.

---

## 12. Usar o seu PC de casa a partir de um notebook (modo cliente)

O modo cliente faz com que o Portal AI Host em um notebook use a IA de outro PC seu, em vez de executar um modelo por conta própria. Os aplicativos do notebook funcionam como de costume.

### Configuração

1. No PC de casa, ative **Permitir que outros dispositivos usem a IA deste PC** e pressione **Parear um dispositivo** (seção 9). Pressione **Copiar link** e envie o link para o notebook.
2. No notebook, abra **Configurações > PC de casa** (ou escolha **Usar meu PC de casa** na janela de boas-vindas).
3. Em **Onde a IA roda**, escolha **Usar meu PC de casa**. Abre-se uma janela **Usar meu PC de casa**. Cole o link com **Colar link**, pressione **Avançar**, confira o nome do PC e a impressão digital, e então **Parear**. Dê um nome ao notebook.
4. O Início agora mostra o nome do PC e um estado: **Conectado - rede doméstica**, **Conectado - via relay**, **Conectando...** ou **Inacessível**.

[screenshot: a janela de pareamento no notebook]

### Configurações (Configurações > PC de casa)

| Configuração | Opções | Padrão |
|---|---|---|
| Onde a IA roda | Este PC / Usar meu PC de casa | Este PC |
| Se o PC de casa estiver inacessível | **Nenhum** (os aplicativos usam a IA embutida deles) / **Usar o modelo deste notebook** (carrega sob demanda) | Nenhum |
| Conexão fora de casa | **Automática** / **Manual** (então uma chave **Usar relay** vale só para esta sessão e é redefinida ao fechar o aplicativo) | Automática |

- Na primeira vez que o PC de casa não puder ser acessado, você é perguntado uma vez o que fazer; pode mudar isso aqui a qualquer momento.
- **Parear PC** pareia de novo; **Desfazer pareamento** esquece o PC (modelos e downloads permanecem como estão); **Trocar PC** (no Início) abre estas configurações; **Executar IA aqui** (no Início) sai do modo cliente e mantém o pareamento salvo.
- Com o Controle de modelos concedido pelo PC de casa, aparece **Modelos em <nome do PC>** com botões de Carregar, Descarregar e posicionamento, além de **Modelo do PC para cada aplicativo**. Sem essa permissão, a página diz: "Peça ao proprietário de <nome do PC> para ativar o Controle de modelos para este notebook".
- Um notebook em modo cliente não aceita conexões de outros dispositivos e não pode servir de ponte até o seu PC de casa.

### O que acontece com os modelos do próprio notebook

Ao entrar no modo cliente, o modelo carregado no notebook é descarregado para liberar memória. Os arquivos de modelo, downloads e a pasta de modelos não são afetados. Ao sair do modo cliente, o modelo volta a carregar quando for solicitado.

### Solução de problemas

- **Inacessível:** verifique se o PC de casa está ligado e na mesma rede, ou ative o relay.
- **"Este notebook foi removido do PC":** o PC de casa o removeu. Escolha **Parear novamente**.
- **"A identidade de <PC> mudou":** o certificado do PC não corresponde mais ao que o notebook salvou. Se você não esperava isso, não pareie novamente. Caso contrário, desfaça o pareamento e depois pareie de novo.
- **O pareamento de fora de casa falha:** pareie primeiro em casa; o link de pareamento precisa apontar para um endereço da sua rede doméstica.
- **Pausado, horário de silêncio, um jogo em execução ou limite diário esgotado:** o PC informa ao notebook o motivo; o notebook usa o seu plano alternativo.

**Onde fica guardado:** o notebook mantém o pareamento na pasta de dados do host, em `client\`. A chave do dispositivo e os tokens são protegidos para o seu usuário do Windows. **Desfazer pareamento** os remove. Backups dessa pasta não podem ser restaurados em outro usuário do Windows.

---

## 13. Fora de casa: o relay e a chave de acesso ao relay

O relay permite que um dispositivo pareado alcance o seu PC de qualquer lugar. **Configurações > Outros dispositivos > Acessar também este PC de fora de casa.**

- O seu PC se conecta para fora a um pequeno serviço de relay. O relay apenas repassa bytes embaralhados que não consegue ler, e não guarda conteúdo. Cada dispositivo ainda precisa estar pareado e assina cada solicitação.
- A linha de estado mostra **Acessível de fora de casa**, **Somente rede doméstica**, **Conectando ao relay...** ou **Não é possível conectar ao relay**.
- Funciona pela porta web habitual, então a maioria das redes de escritório permite.
- **Dispositivos de empresa:** conteúdos de trabalho irão para o seu PC pessoal. Verifique antes as regras do seu empregador.

### Chave de acesso ao relay

**Configurações > Outros dispositivos > Chave de acesso ao relay** (padrão: desativado). Quando ativada, só dispositivos pareados com a chave incluída no código de pareamento podem alcançar este PC pelo relay, o que impede desconhecidos de ocupar os slots do relay do seu PC.

- Ative-a **antes de parear**. Dispositivos pareados antes precisam parear novamente para usar o relay (a rede doméstica continua funcionando).
- **Nova chave** cria uma chave nova após confirmação. Cada dispositivo pareado precisa parear de novo para usar o relay; em casa nada muda. Remover um dispositivo não troca a chave, então use **Nova chave** depois de remover um dispositivo em que você não confia.
- Se o arquivo da chave não puder ser usado, o relay permanece desligado em vez de aberto, e a rede doméstica continua funcionando. Um arquivo de chave danificado é posto de lado com um nome `.bad` e nunca é sobrescrito.
- Se o botão não permanecer ativado, o serviço de relay pode precisar de atualização do lado dele. Desative o botão novamente.

**Limites:** o relay limita a velocidade e a quantidade que um dispositivo pode enviar, para que um único dispositivo não sobrecarregue o serviço. O uso normal fica bem abaixo desses limites.

---

## 14. Privacidade e relatórios de falha

- **Sem análises nem rastreamento de uso.** Apenas um relatório de falha pode sair do seu PC, e somente se você deixar a opção ativada.
- **Seu texto fica nos seus equipamentos.** Prompts e respostas nunca são salvos nem registrados pelo host. Os registros contêm apenas contagens, tempos e estados.
- **Os relatórios de falha vêm ativados por padrão, e você pode desativá-los.** Se a janela ou o host em segundo plano travar, um relatório curto nos ajuda a corrigir o problema. Desative a qualquer momento em **Configurações > Privacidade > Enviar relatórios de falha**; a mudança é imediata. A janela de boas-vindas oferece a mesma escolha, marcada por padrão, antes que qualquer coisa seja enviada.
  - **Enviado:** o tipo do erro e onde ele aconteceu (nomes de funções e módulos), as versões do aplicativo e do Windows, se é uma versão da Store, e se o erro veio da janela ou do host.
  - **Nunca enviado:** prompts ou respostas, o que você digita, nomes de arquivos ou caminhos de pastas, o nome do seu computador ou do usuário, quais aplicativos usam o host, ou o seu endereço IP. O texto do erro é descartado.
  - **Mantidos por 90 dias**, depois apagados.
  - **O que é enviado** (Configurações > Privacidade) abre a página de privacidade.
- **Tudo é criptografado** quando sai deste PC para outro dos seus dispositivos.
- **Política de privacidade:** Sobre > **Política de privacidade**.
- Perguntas, ou um pedido para apagar um relatório: mindthaapp@gmail.com.

[screenshot: Configurações > Privacidade com a chave dos relatórios de falha]

---

## 15. Onde os seus dados ficam guardados, backup e remoção

Tudo fica na pasta de dados do próprio aplicativo. **Sobre > Abrir pasta de dados** a abre.

| O quê | Onde (dentro da pasta de dados) | Fazer backup? |
|---|---|---|
| Modelos baixados | `models\`, ou `Portal AI Host Models\` dentro da pasta que você escolheu nas Configurações | Opcional. Podem ser baixados de novo. São grandes. |
| Configurações | `settings.json` | Sim |
| Aplicativos permitidos e seus tokens | O armazenamento de aplicativos do host | Não portátil (protegido para o seu usuário do Windows) |
| Dispositivos pareados, limites, estatísticas de uso | O armazenamento de dispositivos do host e um pequeno arquivo de estatísticas por dispositivo | Não portátil |
| Pareamento do notebook (modo cliente) | `client\` | Não portátil. Pareie de novo em um PC novo. |
| Registros | `logs\` (rotativos, limitados a 10 MB) | Não |
| Arquivo de descoberta para aplicativos | `discovery\host.json` | Não |

**Backup:** feche o Portal AI Host e copie a pasta de dados. Configurações e modelos podem ser restaurados copiando-os de volta. Permissões de aplicativos, pareamentos de dispositivos e tokens são protegidos para o seu usuário do Windows e para o seu PC, então, em um PC novo, você permite os aplicativos e pareia os dispositivos novamente.

**Remoção:**
- *Um modelo:* **Modelos > Remover**. Somente os arquivos baixados pelo host são apagados.
- *Restos:* **Modelos > Liberar espaço**.
- *Um dispositivo:* **Esquecer** (Dispositivos deste PC) ou **Remover** (Configurações).
- *O acesso de um aplicativo:* **Remover acesso** (Dispositivos e aplicativos).
- *Tudo:* desinstale o aplicativo. Uma pasta de modelos personalizada que você escolheu fica no lugar; apague você mesmo a pasta `Portal AI Host Models` se quiser que ela desapareça.

O aplicativo nunca apaga nada que não tenha criado, nunca sobrescreve os seus arquivos, e um modelo que você adicionou é removido apenas como cópia própria do host (o seu arquivo original não é tocado).

---

## 16. Atalhos de teclado

| Teclas | Ação |
|---|---|
| Ctrl+1 | Início |
| Ctrl+2 | Modelos |
| Ctrl+3 | Dispositivos e aplicativos |
| Ctrl+4 | Experimente |
| Ctrl+, | Configurações |
| F1 | Sobre |
| Ctrl+Enter | Perguntar, em Experimente |
| Esc | Fechar Novidades ou uma caixa de diálogo |

A lista também está no menu do aplicativo > **Atalhos de teclado**.

Aparência: **Configurações > Aparência** oferece Tema (padrão: a sua configuração do Windows), Material da janela (Mica), Cantos (Arredondados), Densidade (Padrão), Tamanho do texto (Normal) e Animações (Seguir o Windows). O modo de alto contraste do Windows sempre usa cores sólidas do sistema.

---

## 17. Solução de problemas e perguntas frequentes

**O host diz que não está em execução.** Pressione **Iniciar o Portal AI Host** no Início. Se ele não conseguir iniciar, reinicie o PC; se isso falhar, reinstale pela Store.

**"Outro usuário do Windows neste PC já está executando o Portal AI Host."** Peça a esse usuário que o feche, ou entre como ele.

**As respostas são lentas.** Veja a seção 7. Um modelo grande demais para a memória de vídeo roda parcialmente no processador.

**Um aplicativo diz que nenhuma IA está disponível.** Confira se o aplicativo está permitido (Dispositivos e aplicativos), se não está pausado, e se há um modelo instalado que não é grande demais para o PC.

**A primeira resposta é lenta, as seguintes são rápidas.** O modelo estava em repouso. Ele carrega na primeira solicitação. Escolha **Manter o modelo carregado** em Configurações > Geral para evitar isso.

**Meus dados são enviados para algum lugar?** Não. Prompts e respostas ficam nos seus dispositivos. Apenas relatórios de falha podem sair (seção 14), e você pode desativá-los.

**Posso usar sem internet?** Sim, depois de baixar um modelo.

**Custa algo?** Não.

**Qual modelo devo escolher?** O recomendado. Escolha um menor se o seu PC parecer lento, ou um maior se quiser respostas melhores e tiver a memória.

**O download travou em "Verificando o download".** Aguarde. Arquivos grandes levam um minuto para ser verificados.

**O pareamento diz "dispositivos demais".** O limite é de 16 dispositivos. Remova um.

**Onde está o ícone da bandeja?** Clique na seta perto do relógio para mostrar os ícones ocultos.

Se ainda precisar de ajuda, use **Sobre > Copiar versão**, escreva para mindthaapp@gmail.com e cole a versão na sua mensagem.

---

## 18. Próximos passos

Estes itens estão planejados e ainda não estão no aplicativo.

- **Categorias de modelos na página Modelos:** agrupadas por tarefa (chat e escrita, programação, raciocínio, visão, embeddings, fala), com filtros e busca.
- **Geração de imagens e vídeos** neste PC, incluindo um "diretor" que encadeia cenas em um vídeo. Os modelos de vídeo aparecerão como "Em breve" até que o mecanismo exista.
- **Escolher qual modelo responde**, e modelos que pedem ajuda uns aos outros, no AI Portal.
- **Versão para macOS.**

---

## 19. Suporte

- **Feedback e sugestões:** Sobre > **Enviar feedback ou sugerir uma ideia**.
- **Dúvidas de suporte e privacidade:** mindthaapp@gmail.com.
- **Versão:** Sobre mostra a versão, e **Copiar versão** a coloca na área de transferência para a sua mensagem.
- **Licenças:** o Portal AI Host inclui o llama.cpp (licença MIT), que executa os modelos no seu PC. Os modelos são baixados sob as próprias licenças (Apache 2.0 ou MIT). Veja Sobre.
