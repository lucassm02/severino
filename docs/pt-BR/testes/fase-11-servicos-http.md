# Guia de testes: Fase 11, Serviços com HTTP

Roteiro manual para fechar a Fase 11. Cobre os critérios de pronto do [spec](../specs/fase-11-servicos-http.md) que os testes automatizados não alcançam. A limpeza dos campos, que entrou junto, está no fim.

## Convenções

- Os comandos são para **PowerShell 7**, num terminal comum.
- Rode a versão de desenvolvimento com `dotnet run --project src/Severino.App`, depois de sair do Severino instalado pela bandeja.
- Com a CA ativa (Configurações › HTTPS).
- Um servidor HTTP qualquer na porta 3000 faz o papel do destino:

  ```powershell
  npx --yes http-server -p 3000
  ```

## Portas HTTP

1. **Marcar.** Em Serviços, crie `catalogoapi` com a porta `8080 → 127.0.0.1:3000` e marque **HTTP** na linha.
   - Esperado: a seção **HTTP e HTTPS** explica o que muda; **Usar HTTPS** fica disponível. Desmarcando HTTP, ele fica cinza e aparece "Marque uma porta como HTTP para usar o HTTPS."
2. **Pelo proxy.** Salve e rode:

   ```powershell
   curl.exe -i http://catalogoapi:8080/
   ```

   - Esperado: a resposta do servidor. Em Requisições aparece uma linha HTTP `catalogoapi:8080`, não uma linha TCP. Nos detalhes, **Rota** mostra `catalogoapi`.
3. **Host do app.** Nos detalhes da requisição, aba Cabeçalhos.
   - Esperado: o destino recebeu `Host: catalogoapi:8080`, como o app mandou.
4. **HTTPS.** Edite, ligue **Usar HTTPS** e salve. Se a barra de status avisar nome fora da CA, reemita por ali.
   - Esperado: o texto abaixo da caixa diz "https://catalogoapi vai para a porta 8080…".

   ```powershell
   curl.exe -i https://catalogoapi/
   curl.exe -i https://catalogoapi:8080/
   curl.exe -i http://catalogoapi:8080/
   ```

   - Esperado: as três respondem, as duas primeiras com certificado do Severino, sem `-k`. O card mostra o selo `https`.
5. **Regras.** Edite, abra **Regras**, ligue CORS e ponha a resposta `X-Ambiente: dev`.
   - Esperado: o card mostra os selos `CORS` e `+1 cabeçalho`, e `curl.exe -i http://catalogoapi:8080/` traz `X-Ambiente: dev`.
6. **Regra sem HTTP.** Desmarque HTTP em todas as portas, deixe CORS ligado e salve.
   - Esperado: o erro "As regras só valem nas portas marcadas como HTTP…"; nada é salvo.
7. **Porta TCP continua túnel.** Num serviço com uma porta de banco (5432) sem a marca, conecte um cliente.
   - Esperado: a linha TCP de sempre em Requisições.
8. **Atualizar.** Num serviço importado do Docker ou do Kubernetes, marque HTTP numa porta e clique **Atualizar** no grupo.
   - Esperado: a marca continua na mesma porta.
9. **WSL.** Numa distro com o serviço chamado pelo nome, `curl http://catalogoapi:8080/`.
   - Esperado: responde, direto do destino. Não aparece em Requisições (o WSL não passa pelo Windows), como diz a seção HTTP do editor.

## Abrir no navegador

10. **Uma porta.** Clique no nome de `catalogoapi` (só a 8080, com HTTPS).
    - Esperado: abre `https://catalogoapi/`.
11. **Várias.** Ponha também `9090 → 127.0.0.1:3000` com HTTP e clique no nome.
    - Esperado: um menu com `https://catalogoapi/` e `https://catalogoapi:9090/`. O **⋯** do card tem **Abrir no navegador** com os mesmos.
12. **Banco.** Um serviço só com 5432 ou 6379.
    - Esperado: o nome não é link e o ⋯ não tem **Abrir no navegador**.

## Campos que se limpam

13. **Destino colado.** No editor de rota, cole `https://192.168.100.5:8000/api` no destino.
    - Esperado: fica `192.168.100.5`, a porta vira 8000 e o esquema, https.
14. **Dois-pontos sobrando.** Digite `192.168.100.5:` no destino e saia do campo.
    - Esperado: fica `192.168.100.5`.
15. **Porta.** Cole `:9090` na porta (rota, serviço ou Configurações).
    - Esperado: `9090`.
16. **Nomes.** No editor de serviço ou DNS, cole `http://pedidos:8080/, pedidos.loja.` nos nomes e saia do campo.
    - Esperado: `pedidos` e `pedidos.loja`, um por linha.

## Segurança nas rotas

Com uma rota `meuapp.sev → localhost:3000`.

17. **Tela de senha.** No editor, em **Segurança**, escolha **Tela de senha do Severino**, usuário `ana`, senha `segredo`, e salve. Abra `http://meuapp.sev/pagina?x=1`.
    - Esperado: a página do Severino pede usuário e senha. Com a senha errada, "Usuário ou senha errados."; com a certa, volta para `/pagina?x=1`. O card mostra o selo `senha`.
18. **Fora do navegador.** `curl.exe -i http://meuapp.sev/`.
    - Esperado: 401 em texto, com o endereço da tela; em Requisições, o selo Severino.
19. **Senha nova encerra a sessão.** Edite a rota e digite outra senha. Recarregue a página no navegador.
    - Esperado: a tela de senha de novo. Editar sem digitar senha mantém a atual.
20. **Sair.** Abra `http://meuapp.sev/.severino/logout`.
    - Esperado: volta para a tela de senha.
21. **Basic Auth.** Troque para **Basic Auth** e abra a rota.
    - Esperado: o navegador pergunta usuário e senha; `curl.exe -u ana:segredo http://meuapp.sev/` responde.
22. **Cabeçalho.** Troque para **Cabeçalho obrigatório**, `X-Api-Key` com `chave-123`.
    - Esperado: `curl.exe -i http://meuapp.sev/` dá 401; `curl.exe -i -H "X-Api-Key: chave-123" http://meuapp.sev/` responde, e o destino recebe o cabeçalho.
23. **Sem senha em texto.** Abra `%LOCALAPPDATA%\Severino\config.json`.
    - Esperado: só `passwordHash` e `headerHash`; nem `segredo` nem `chave-123` aparecem.
