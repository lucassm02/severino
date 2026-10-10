# Guia de testes: Fase 6, DNS e extras

Roteiro manual para fechar a Fase 6. Cobre os critérios de pronto do [spec](../specs/fase-6-dns-e-extras.md): a aba DNS e as linhas do hosts que não são do Severino, a integração com rotas e serviços, o curinga, rotas por caminho, grupos, o `kubectl port-forward`, o acompanhamento automático e o módulo PowerShell.

Cada teste diz o que fazer, o que tem de acontecer e qual critério ele prova. Anote o resultado na tabela do fim.

## Convenções

- Os comandos marcados como **PowerShell** rodam num terminal comum; **Admin** num terminal como administrador; **WSL** dentro de `wsl -d Ubuntu-22.04`.
- Os nomes dos exemplos (`sql.interno`, `gateway.k8s`, `lojas.sev`) são de teste: troque pelos seus, e apague o que criar no fim.
- Para fechar o Severino, use **Sair** na bandeja. Fechar a janela só esconde o app.
- O teste 10 precisa da VPN. Com ela ligada, o GitHub não responde nesta máquina.

## 0. Preparação

1. **Testes automatizados** (critério 7). No **PowerShell**:

   ```powershell
   dotnet build
   dotnet test
   ```

   Esperado: `0 Aviso(s)`, `0 Erro(s)` e todos aprovados.
2. **Helper de desenvolvimento.** O protocolo mudou para a versão 3, e o Helper agora também é o DNS dos curingas. No **Admin**, na pasta do projeto:

   ```powershell
   ./scripts/dev-helper.ps1 install
   ```

3. **Um retrato do hosts**, para comparar no fim. No **PowerShell**:

   ```powershell
   Copy-Item C:\Windows\System32\drivers\etc\hosts $env:TEMP\hosts-antes-fase6
   ```

4. Abra o app (`dotnet run --project src/Severino.App`). A barra inferior não pode mostrar "serviço auxiliar de outra versão".

## 1. A aba DNS e as linhas de fora

Prova os critérios 1 e 2.

1. Abra a aba **DNS**. Esperado:
   - em **No hosts, fora do Severino**, as linhas que você escreveu à mão, cada uma com o selo "fora do Severino" e a barra laranja à esquerda; uma linha que o Severino já editou leva também o selo "editada pelo Severino";
   - linhas dentro de blocos de outros programas mostram "Perto de:" com o comentário do bloco.
2. **Nova entrada**: nome `sql-teste.interno`, endereço `10.123.0.8`. Esperado: "rede privada: vai para o hosts na hora", e a entrada aparece "Ativa".
3. No **PowerShell**:

   ```powershell
   Resolve-DnsName sql-teste.interno -Type A | Select-Object IPAddress
   ```

   No **WSL**:

   ```bash
   getent ahostsv4 sql-teste.interno | head -1
   ```

   Esperado: `10.123.0.8` nos dois.
4. Desligue a entrada: o nome deixa de resolver. Ligue de novo: volta.
5. **Persistência.** Bandeja › **Pausar**: `sql-teste.interno` continua resolvendo. **Retomar**. **Sair**: continua resolvendo com o app fechado. Abra o app de novo.
6. **Remover** com **Desfazer**: a entrada volta.

## 2. IP público e aprovação

Prova a decisão 1 (opção C).

1. **Nova entrada**: `publico-teste.sev` → `203.0.113.10`. Esperado: ao salvar, o UAC do Windows pede para aprovar o "Severino.Helper".
2. **Cancele** no UAC. Esperado: a entrada fica "Aguardando aprovação", e a faixa no topo da aba oferece **Aprovar**. `Resolve-DnsName publico-teste.sev` não acha o nome.
3. **Aprovar** e confirme no UAC. Esperado: "Aprovado: já está no hosts", e o nome resolve para `203.0.113.10`.

## 3. Editar e remover uma linha de fora

Prova o critério 2. Faça com uma linha de teste, não com uma sua de verdade.

1. No **Admin**, acrescente uma linha de teste ao hosts:

   ```powershell
   Add-Content C:\Windows\System32\drivers\etc\hosts "`r`n10.123.0.50 linha-teste.interno"
   ```

   Esperado: em até 1 s, ela aparece na aba DNS, entre as de fora.
2. ⋯ › **Editar linha que não é do Severino…**. Esperado: a faixa de aviso fixa no topo ("Você está mexendo numa linha que já existia no hosts") e a linha atual.
3. Mude o endereço para `10.123.0.51` e clique em **Revisar e alterar o hosts**. Esperado: a confirmação mostra "Como está" e "Como vai ficar", com o comentário. Confirme.
4. **Abrir o hosts** (no topo da aba) abre o Bloco de Notas como administrador. Esperado no arquivo:

   ```
   # Severino: esta linha nao foi criada pelo Severino; editada em <data e hora>. Antes: 10.123.0.50 linha-teste.interno
   10.123.0.51  linha-teste.interno
   ```

5. Edite de novo para `10.123.0.52`. Esperado: um comentário só, com o "Antes" ainda em `10.123.0.50`.
6. ⋯ › **Remover linha que não é do Severino…** › **Comentar a linha**. Esperado: a linha vira `# 10.123.0.52 linha-teste.interno` sob o aviso "removida em", e na aba aparece apagada, "removida por ele".
7. Apague as duas linhas de teste do hosts no Bloco de Notas.

## 4. Um nome, um dono

Prova o critério 5.

1. **Nova rota** com o domínio `sql-teste.interno`. Esperado: "Já existe uma entrada DNS com este nome."
2. **Nova entrada** DNS com o nome de uma das suas linhas de fora. Esperado: "… já está no hosts, fora do Severino. Edite essa linha na aba DNS ou escolha outro nome."

## 5. Nomes como destino

Prova o critério 4.

1. **Nova entrada**: `destino-teste.interno` → `127.0.0.1`. Suba algo na porta 8000 (no **PowerShell**: `python -m http.server 8000`).
2. **Nova rota** `destino.sev`. No host do destino, abra a lista: `destino-teste.interno` aparece, com o IP. Escolha-o, porta `8000`. Esperado: `http://destino.sev/` abre a listagem do Python.
3. Na aba DNS, a entrada mostra "Destino de 1 rota", com "1 rota" como link: clicar abre a aba Rotas filtrada por esse nome. Na aba Rotas, a linha de `destino.sev` mostra o IP ao lado do destino. Remover a entrada pede confirmação e lista `destino.sev`. Cancele.
4. ⋯ › **Transformar em rota…** numa entrada DNS: abre o formulário de rota com o nome e o IP preenchidos. Cancele: a entrada continua.
5. Numa rota cujo destino é um IP, ⋯ › **Transformar em entrada DNS…**: a confirmação explica que a porta, o HTTPS e o log deixam de valer. Cancele.

## 6. Curinga

Prova o critério do curinga.

1. **Nova rota** `*.lojas.sev` → `http://localhost:8000`, com HTTPS. Esperado: a aba Rotas mostra "*.lojas.sev só vale depois de aprovado…" com **Aprovar**.
2. **Aprovar** e confirme no UAC. No **PowerShell**:

   ```powershell
   Get-DnsClientNrptRule | Where-Object Comment -eq Severino | Select-Object Namespace, NameServers
   Resolve-DnsName cliente42.lojas.sev | Select-Object IPAddress
   ```

   Esperado: a regra `.lojas.sev → 127.53.0.1`, e o nome resolvendo para `127.0.0.1` e `::1`.
3. Abra `https://cliente42.lojas.sev/` e `https://a.b.lojas.sev/` no navegador. Esperado: a listagem do Python, com cadeado.
4. No **WSL**: `getent ahosts cliente42.lojas.sev | head -1`. Esperado: `::1` ou `127.0.0.1`.
5. **Pausar**: `cliente42.lojas.sev` deixa de resolver, e a regra NRPT sai. **Retomar**: volta.
6. **Entrada DNS curinga**: `*.dev-teste.interno` → `10.123.0.9`, aprovando no UAC. Esperado: `x.dev-teste.interno` resolve para `10.123.0.9`, e continua resolvendo com o app fechado.
7. **Com a VPN ligada**, repita o passo 2. Esperado: o curinga continua resolvendo. Se não resolver, anote as regras NRPT da VPN (`Get-DnsClientNrptRule`).

## 7. Rotas por caminho

1. Rota `caminho.sev` → `http://localhost:8000` e outra rota com o endereço `caminho.sev/api` → `http://localhost:8001`, com **Tirar /api antes de repassar** (a caixa só aparece quando o endereço tem caminho).
2. Suba algo na 8001 (`python -m http.server 8001` noutra pasta). Esperado:
   - `http://caminho.sev/api/` lista a pasta da 8001;
   - `http://caminho.sev/` e `http://caminho.sev/apix` vão à 8000.
3. Na lista, a segunda rota aparece como `caminho.sev/api`, e "Abrir no navegador" abre `/api`.

## 8. Grupos

1. Edite duas rotas e ponha o grupo `teste` (campo **Grupo**). Esperado: elas aparecem sob o cabeçalho **teste**, com a contagem, uma chave do grupo e o menu "…" (Nova rota neste grupo, Renomear grupo, Desfazer o grupo).
2. Desligue a chave do grupo. Esperado: as duas desligam de uma vez, e os domínios saem do hosts.

## 9. Acompanhar sozinho

Prova o critério de acompanhar.

1. Com o callfred importado do Docker (Fase 4), no **WSL**, recrie o container em outra porta publicada.
2. Esperado, em poucos segundos e sem clicar em nada: o aviso "orchestrator com portas novas, atualizado sozinho", e a porta nova na linha do serviço.
3. Configurações › **Acompanhar serviços importados** desligado: recriar de novo não muda nada até o **Atualizar**.

## 10. `kubectl port-forward` (precisa da VPN)

Prova o critério do port-forward.

1. Serviços › **Importar**: um service só ClusterIP (como `kube-dns`, ou um `ClusterIP` do seu cluster) agora tem caixa, com "<porta> → kubectl port-forward, que o Severino mantém rodando". "Marcar o namespace inteiro" não o marca. Marque-o à mão e importe.
2. Esperado: a linha do serviço mostra "<porta> → port-forward" e "kubectl port-forward rodando"; no editor, as portas apontam para `127.0.0.1:42000` em diante.
3. Chame o serviço pelo nome e porta originais (`curl.exe http://<service>.<ns>:<porta>/`, ou `Test-NetConnection`). Esperado: responde.
4. **Desligue a VPN.** Esperado: "port-forward reiniciando" com o erro do `kubectl`. **Ligue a VPN.** Esperado: volta a "rodando" sozinho, em até 30 s.
5. Desligue o serviço. No **WSL**: `pgrep -af severino-pf` não mostra nada.

## 11. Módulo PowerShell

1. No **PowerShell**, com o app aberto:

   ```powershell
   Import-Module .\powershell\Severino
   New-SeverinoRoute ps.sev http://localhost:8000 -Group teste
   Set-SeverinoDns ps-teste.interno 10.123.0.77
   Get-SeverinoRoute ps.sev
   Get-SeverinoDns | Format-Table
   Disable-SeverinoRoute -Group teste
   ```

   Esperado: a rota e a entrada aparecem no app na hora; o grupo desliga.
2. `Remove-SeverinoRoute ps.sev` e `Remove-SeverinoDns ps-teste.interno` removem. Com o app fechado, qualquer comando diz "O Severino não está aberto".
3. Instalado pelo instalador, o módulo carrega sem caminho (`Import-Module Severino`), no Windows PowerShell e no PowerShell 7.

## 12. Limpar tudo e desinstalar

Prova o critério 3.

1. Configurações › **Limpar tudo** (sem apagar as rotas). Esperado depois que o app fecha:
   - o hosts não tem mais os blocos `# >>> Severino` nem `# >>> Severino DNS`;
   - as linhas de fora ficam como você as deixou;
   - `Get-DnsClientNrptRule | Where-Object Comment -eq Severino` não mostra nada (o Helper as tira quando os curingas somem).
2. Para o desinstalador: além disso, `reg query HKLM\SOFTWARE\Severino\Helper\ApprovedDns` não existe mais.
3. Compare com o retrato do passo 0: só podem ter mudado as linhas de teste que você mesmo criou e apagou.

## 13. Organização da UI

Prova a [revisão da UI](../specs/revisao-ui.md).

1. As abas são **Rotas, Serviços, DNS, Requisições, Configurações**, e cada uma das três primeiras diz embaixo do título para que serve. Com uma configuração vazia (pasta de configuração nova), Rotas e Serviços mostram o quadro "Rota, serviço ou DNS: qual usar".
2. **Nova rota** com o destino `localhost:5432`. Esperado: o aviso de que 5432 é a porta do PostgreSQL, com **Criar como serviço**. Clicar abre o editor de serviço já com o nome e a porta; ao salvar, o aviso "virou um serviço" com **Ver em Serviços**.
3. **Nova rota** `sql2.interno` com o destino `http://10.123.0.9:80`. Esperado: **Criar entrada DNS**, que abre o editor de DNS já preenchido. Cancele.
4. Na aba Serviços, um serviço que vai para `gateway.k8s` mostra "gateway.k8s é <IP>" como link, que abre o DNS filtrado. Na aba DNS, "Destino de N serviços" abre a aba Serviços filtrada.
5. Importação com Ingress marcado: o rodapé conta "… serviços marcados · 1 rota de Ingress", e o aviso no fim tem **Ver em Rotas**.
6. Configurações › **Backup** › Exportar. Abra o JSON: tem `routes`, `services` e `dnsEntries`, com `"severino": 2`. Em outra pasta de configuração (ou depois de apagar uma entrada), **Importar** traz o que falta e lista o que ficou de fora por nome repetido.
7. Configurações › **Terminal**: com o instalador, diz "Instalado"; **Copiar** põe os três exemplos na área de transferência.

## Limpeza

- Apague as entradas, rotas e o grupo de teste.
- Feche os servidores `python -m http.server`.
- Reinstale o Helper de desenvolvimento, se fez o passo 2 do teste 12.

## Registro de resultados

| # | Teste | Critério | Resultado | Observações |
|---|---|---|---|---|
| 0 | Build sem avisos e testes automatizados | 7 | ☐ | |
| 1 | Aba DNS e linhas de fora | 1, 2 | ☐ | |
| 2 | IP público e aprovação | decisão 1 | ☐ | |
| 3 | Editar e remover linha de fora | 2 | ☐ | |
| 4 | Um nome, um dono | 5 | ☐ | |
| 5 | Nomes como destino | 4 | ☐ | |
| 6 | Curinga | curinga | ☐ | |
| 7 | Rotas por caminho | caminho | ☐ | |
| 8 | Grupos | grupos | ☐ | |
| 9 | Acompanhar sozinho | acompanhar | ☐ | |
| 10 | kubectl port-forward | port-forward | ☐ | |
| 11 | Módulo PowerShell | PowerShell | ☐ | |
| 12 | Limpar tudo e desinstalar | 3 | ☐ | |
| 13 | Organização da UI | revisão da UI | ☐ | |
