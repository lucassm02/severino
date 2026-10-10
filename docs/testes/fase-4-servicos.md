# Guia de testes: Fase 4, Serviços (Kubernetes, Docker e WSL)

Roteiro manual para fechar a Fase 4. Cobre os critérios de pronto do [spec](../specs/fase-4-servicos.md) contra o cenário de referência: o cluster de `staging` pela VPN e o Docker da distro `Ubuntu-22.04`.

Cada teste diz o que fazer, o que tem de acontecer e qual critério ele prova. Anote o resultado na tabela do fim.

## Convenções

- Os comandos marcados como **PowerShell** rodam num terminal comum do Windows. Os marcados como **WSL** rodam dentro da distro, num terminal aberto com `wsl -d Ubuntu-22.04`.
- `curl.exe`, com `.exe`, é o curl do Windows. No PowerShell, `curl` sem `.exe` é outro comando.
- Para fechar o Severino, use **Sair** no ícone da bandeja. Fechar a janela só esconde o app.
- Os endereços `127.77.x.y` são dados na ordem da importação. Os exemplos supõem que `algaractivationmicroservice` é o primeiro serviço importado, em `127.77.0.2`. Confira o seu na aba Serviços: fica na dica de ferramenta das portas de cada serviço, e no editor.

## 0. Preparação

1. **Testes automatizados** (critério 9):

   ```powershell
   dotnet build
   dotnet test
   ```

   Esperado: `0 Aviso(s)`, `0 Erro(s)` e todos os testes aprovados.
2. **Helper de desenvolvimento.** O protocolo do pipe mudou para a versão 2, e o Helper antigo não fala com o app novo. Num terminal **como administrador**, na pasta do projeto:

   ```powershell
   ./scripts/dev-helper.ps1 install
   ```

3. **VPN conectada.** Com ela ligada, o GitHub não responde nesta máquina; faça qualquer push antes.
4. **Distro rodando** com o container do meuapp de pé:

   ```powershell
   wsl -d Ubuntu-22.04 -- docker ps --format "{{.Names}} {{.Ports}}"
   ```

   Esperado: `meuapp-orchestrator-1 0.0.0.0:24600->4000/tcp` (o nome pode variar).
5. **Referência direta**, para comparar depois. No **PowerShell**:

   ```powershell
   curl.exe -s -o NUL -w "%{http_code}\n" http://192.168.203.100:32366/
   Test-NetConnection 192.168.203.100 -Port 30711 | Select-Object TcpTestSucceeded
   ```

   Anote o código HTTP; `TcpTestSucceeded` tem de ser `True`. Se algum falhar, o problema é a VPN ou o cluster, não o Severino.
6. Abra o app:

   ```powershell
   dotnet run --project src/Severino.App
   ```

   A barra inferior não pode mostrar "serviço auxiliar de outra versão". Se mostrar, refaça o passo 2.

## 1. Descoberta no WSL

Prova o critério 1.

1. Serviços › **Importar**. Esperado:
   - a janela abre em **WSL · Ubuntu-22.04**, sem nenhum comando digitado;
   - ao lado de **Kubernetes**: `kubernetes-admin@kubernetes · 119 services, 111 com acesso de fora` (os números podem ter mudado);
   - ao lado de **Docker**: `1 container rodando`;
   - **Endereço do cluster** em `192.168.203.100`.
2. Na lista, confira:
   - `postgres  database · NodePort` com `5432 → 192.168.203.100:30711` e os quatro nomes, de `postgres` a `postgres.database.svc.cluster.local`;
   - `orchestrator  meuapp · Docker` com `4000 → 127.0.0.1:24600`;
   - `kubernetes  default · ClusterIP` apagado, sem caixa, com "Só ClusterIP: não tem acesso de fora do cluster.";
   - services sem pods com o aviso laranja "Nenhum pod pronto agora".
3. **Filtros.** Escolha o namespace `staging`. Busque `algar`. Esperado: só os services do `staging` com "algar" no nome. O rodapé diz "0 de N marcados · M na lista".
4. **Caixas de ferramenta.** Desmarque **Docker**: o `orchestrator` some e nada é perguntado ao docker. Marque de novo: ele volta. Feche e abra a janela: as caixas ficam como você deixou.
5. Feche com **Cancelar**.

## 2. Descoberta no Windows

Prova o critério 2, no que dá para provar nesta máquina, que não tem `kubectl` nem `docker` no Windows.

1. Importar serviços › **Procurar em** › **Windows**. Esperado: "O kubectl não está instalado em Windows." e "O docker não está instalado em Windows.", sem travar a janela.
2. Se houver outra distro instalada e parada, ela aparece como "(parada; procurar inicia a distro)". Escolhê-la inicia a distro e procura nela. Pular é aceitável.

A parte do Docker Desktop visto do Windows e das distros não se aplica: esta máquina não tem Docker Desktop.

## 3. Importar e chamar do Windows

Prova os critérios 4 e 5.

1. Importar serviços. Em `staging`, marque `algaractivationmicroservice`. Em `database`, marque `postgres`. Em `meuapp`, marque `orchestrator`. Esperado: o botão mostra **Importar 3**.
2. **Importar 3**. Esperado:
   - o aviso "3 serviços importados. Apps no WSL · Ubuntu-22.04 também chamam pelos nomes.";
   - na aba **Serviços**, dois grupos: "Kubernetes · kubernetes-admin@kubernetes · WSL · Ubuntu-22.04" e "Docker · WSL · Ubuntu-22.04";
   - em cada serviço, as portas (`5432 → 192.168.203.100:30711`), com o endereço `127.77.0.x` na dica de ferramenta, e em alguns segundos "Respondendo".
3. **HTTP pela NodePort, com o nome intacto.** No **PowerShell**:

   ```powershell
   curl.exe -s -o NUL -w "%{http_code}\n" http://algaractivationmicroservice/
   curl.exe -s -o NUL -w "%{http_code}\n" http://algaractivationmicroservice.staging.svc.cluster.local/
   curl.exe -v http://algaractivationmicroservice.staging/ 2>&1 | Select-String "Host:|Trying"
   ```

   Esperado:
   - os dois primeiros com o mesmo código do passo 5 da preparação;
   - no terceiro, `Trying 127.77.0.2:80` e `Host: algaractivationmicroservice.staging`, o nome que saiu do app.
4. **TCP puro, Postgres.** No **PowerShell**:

   ```powershell
   Test-NetConnection postgres.database -Port 5432 | Select-Object RemoteAddress, TcpTestSucceeded
   ```

   Esperado: `RemoteAddress` `127.77.0.3` e `TcpTestSucceeded` `True`. Com o `psql` instalado, `psql -h postgres.database -p 5432 -U <usuário>` conecta.
5. **Docker pelo nome do Compose.** No **PowerShell**:

   ```powershell
   curl.exe -s -o NUL -w "%{http_code}\n" http://orchestrator:4000/
   ```

   Esperado: o mesmo código de `http://localhost:24600/`.
6. **Requisições.** Na aba Requisições, cada conexão dos passos 3 a 5 aparece como `TCP`, com `nome:porta` e `→ destino`.

## 4. Chamar de dentro do WSL

Prova o critério 6.

1. Configurações › **WSL**. Esperado: `Ubuntu-22.04` ligada, com "3 serviços pelo nome".
2. No **WSL**:

   ```bash
   getent hosts algaractivationmicroservice postgres.database orchestrator
   curl -s -o /dev/null -w "%{http_code}\n" http://algaractivationmicroservice/
   curl -s -o /dev/null -w "%{http_code}\n" http://algaractivationmicroservice.staging.svc.cluster.local/
   timeout 5 bash -c 'echo > /dev/tcp/postgres.database/5432' && echo postgres-ok
   curl -s -o /dev/null -w "%{http_code}\n" http://orchestrator:4000/
   ```

   Esperado:
   - o `getent` mostra os mesmos endereços `127.77.0.x` do Windows;
   - os `curl` dão os mesmos códigos do teste 3, e aparece `postgres-ok`.
3. **O que foi gravado.** No **WSL**:

   ```bash
   sed -n '/# >>> Severino/,/# <<< Severino/p' /etc/hosts
   sudo iptables -t nat -S SEVERINO
   cat /proc/sys/net/ipv4/conf/all/route_localnet
   ```

   Esperado: o bloco com os nomes, uma regra `DNAT` por porta (por exemplo, `--dport 5432 ... --to-destination 192.168.203.100:30711`) e `1`.

## 5. A distro reiniciada recebe os nomes de novo

Prova o item do checklist "a distro reiniciada mantém o bloco".

1. Com o Severino aberto, no **PowerShell**:

   ```powershell
   wsl --terminate Ubuntu-22.04
   ```

   Esperado: Configurações › WSL mostra "Parada. Recebe os nomes quando iniciar." em até 10 s.
2. Abra a distro de novo (`wsl -d Ubuntu-22.04`). Em até 10 s, rode no **WSL** os comandos do teste 4, passo 2. Esperado: tudo funciona, sem você fazer nada no Severino.

O container do meuapp volta junto se ele tiver `restart: unless-stopped` no Compose. Se não voltar, suba-o antes de testar o `orchestrator`.

## 6. Atualizar

Prova o critério 7. Usa um container de teste para não mexer no meuapp.

1. No **WSL**:

   ```bash
   docker run -d --name sev-teste -p 18080:80 nginx
   ```

2. Importar serviços › marque `sev-teste` (projeto "(sem projeto)") › **Importar 1**. No **PowerShell**, `curl.exe -s -o NUL -w "%{http_code}\n" http://sev-teste/` dá `200`.
3. Recrie o container em outra porta. No **WSL**:

   ```bash
   docker rm -f sev-teste && docker run -d --name sev-teste -p 18081:80 nginx
   ```

   O `curl` do passo 2 agora falha, e o serviço fica "Fora do ar".
4. No grupo "Docker · WSL · Ubuntu-22.04", clique **Atualizar**. Esperado:
   - o aviso "1 serviço com portas novas";
   - a linha mostra `80 → 127.0.0.1:18081`, e o nome `sev-teste` continua;
   - o `curl` volta a dar `200`, no Windows e no WSL.
5. **Nomes editados ficam.** No serviço, ⋯ › **Editar**. Apague a linha `sev-teste` dos nomes, deixe só um nome novo, `nginx-teste`, e salve. Recrie o container na porta 18082 e clique **Atualizar** de novo. Esperado: a porta vira 18082, e o nome continua `nginx-teste`.
6. **Contexto errado.** Pule este passo se só houver um contexto no `kubectl`. Com outro contexto ativo, **Atualizar** no grupo do Kubernetes avisa que o kubectl está em outro contexto e não muda nada.

## 7. Conflitos de nome

Prova a regra de nomes da spec.

1. **Nova rota** (web) com o domínio `postgres.database`. Esperado: "Já existe uma rota de serviço com este nome.", e não salva.
2. Importar serviços. Se o cluster tiver dois services com o mesmo nome em namespaces diferentes, marque os dois. Esperado: o segundo avisa "<nome> já está em outra rota e fica de fora" e importa com os nomes mais longos. Se não houver, pule.
3. Importar de novo `postgres`, já importado. Esperado: a linha diz "Já importado: importar de novo atualiza as portas", e importar não cria um segundo `postgres`.

## 8. Pausar e sair

1. Bandeja › **Pausar**. Esperado:
   - no **PowerShell**, `Test-NetConnection postgres.database -Port 5432` falha, porque o nome não resolve mais;
   - no **WSL**, `getent hosts postgres.database` não mostra nada, e `sudo iptables -t nat -S SEVERINO` diz que a cadeia não existe.
2. **Retomar**. Esperado: os dois voltam a funcionar em poucos segundos.
3. **Sair** do Severino. Esperado: o bloco some do `/etc/hosts` da distro, e `route_localnet` volta a `0`. Abra o app de novo: os nomes voltam.

## 9. Configurações › WSL

1. Desligue `Ubuntu-22.04`. Esperado: em alguns segundos, o bloco e as cadeias saem da distro, e o Windows continua resolvendo os nomes.
2. Ligue de novo. Esperado: "3 serviços pelo nome" (ou quantos houver) e tudo de volta.
3. **Aplicar de novo** regrava a distro. No **WSL**, apague a cadeia à mão com `sudo iptables -t nat -F SEVERINO`, clique **Aplicar de novo** e confira que as regras voltaram.

## 10. Colar a saída

Prova o critério 3. Desligar a VPN para este teste é opcional; ele vale com ou sem.

1. No **PowerShell**:

   ```powershell
   wsl -d Ubuntu-22.04 -- bash -lc "kubectl get svc -A -o json" | Set-Clipboard
   ```

2. Importar serviços › **Procurar em** › **Colar saída de comando**. Cole no campo, preencha o IP de um nó com `192.168.203.100` e clique **Ler**. Esperado: a mesma lista do teste 1, com os mesmos destinos NodePort.
3. Apague o IP do nó e clique **Ler** de novo. Esperado: os services NodePort ficam sem caixa, com "Escolha um nó para usar a NodePort.".
4. Cole um texto qualquer e clique **Ler**. Esperado: "Não reconheci o texto…".
5. Com a VPN desligada, abra a importação com **Kubernetes** marcado. Esperado: "O cluster não respondeu em 10 s. A VPN está conectada?", sem travar. Desmarque **Kubernetes**: a lista mostra só o Docker, sem esperar.

## 11. Remover

Prova o critério 8.

1. No grupo do Kubernetes, ⋯ › **Remover todos deste grupo**. Esperado: "2 serviços removidos", com **Desfazer**. Clique **Desfazer**: eles voltam, com os mesmos endereços.
2. Remova de novo e espere o **Desfazer** sumir. Remova também os serviços do Docker. Esperado:
   - no **PowerShell**, os nomes saíram do hosts do Windows:

     ```powershell
     Select-String -Path C:\Windows\System32\drivers\etc\hosts -Pattern "127\.77\."
     ```

     O comando não mostra nada;
   - as portas foram liberadas:

     ```powershell
     Get-NetTCPConnection -State Listen | Where-Object LocalAddress -like "127.77.*"
     ```

     O comando não mostra nada;
   - no **WSL**, o bloco e as cadeias saíram, como no teste 8.

## 12. Limpar tudo e desinstalar (opcional)

Remove também a CA e a inicialização automática. Faça só se quiser refazer o teste do instalador.

1. Importe um serviço de novo. Configurações › **Limpar tudo…** › **Limpar e fechar**. Esperado: além do que a Fase 3 já cobre, o bloco sai do `/etc/hosts` da distro.
2. Para o instalador: com um serviço importado, desinstale. O `--cleanup` do desinstalador passa pelas distros marcadas, inclusive as paradas. Esperado: nenhum bloco Severino no `/etc/hosts` da distro depois.

## Limpeza

- `wsl -d Ubuntu-22.04 -- docker rm -f sev-teste`.
- Reinstale o Helper de desenvolvimento, se fez o teste 12.

## Registro de resultados

| # | Teste | Critério | Resultado | Observações |
|---|---|---|---|---|
| 0 | Build sem avisos e testes automatizados | 9 | ☐ | |
| 1 | Descoberta no WSL | 1 | ☐ | |
| 2 | Descoberta no Windows | 2 | ☐ | |
| 3 | Importar e chamar do Windows | 4, 5 | ☐ | |
| 4 | Chamar de dentro do WSL | 6 | ☐ | |
| 5 | Distro reiniciada | checklist | ☐ | |
| 6 | Atualizar | 7 | ☐ | |
| 7 | Conflitos de nome | spec | ☐ | |
| 8 | Pausar e sair | spec | ☐ | |
| 9 | Configurações › WSL | spec | ☐ | |
| 10 | Colar a saída | 3 | ☐ | |
| 11 | Remover | 8 | ☐ | |
| 12 | Limpar tudo e desinstalar | spec | ☐ | |
