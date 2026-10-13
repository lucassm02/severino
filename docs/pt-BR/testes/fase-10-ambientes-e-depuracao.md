# Guia de testes: Fase 10, Ambientes, depuração e projetos

Roteiro manual para fechar a Fase 10. Cobre os critérios de pronto do [spec](../specs/fase-10-ambientes-e-depuracao.md) que os testes automatizados não alcançam. Cada parte ganha a sua seção quando fica pronta; as que ainda não existem estão marcadas como pendentes.

## Convenções

- Os comandos são para **PowerShell 7**, num terminal comum.
- Rode a versão de desenvolvimento com `dotnet run --project src/Severino.App`, depois de sair do Severino instalado pela bandeja.
- Os exemplos usam uma rota `meuapp.sev → localhost:3000`. Qualquer servidor serve; para ter um rápido, numa pasta vazia:

  ```powershell
  npx --yes http-server -p 3000
  ```

## Parte 2. Detalhes de uma requisição

Prova os critérios 1 a 7 da parte 2.

1. **Abrir.** Abra `http://meuapp.sev/` no navegador. Na aba Requisições, clique na linha.
   - Esperado: um painel à direita com **Geral**, **Cabeçalhos** e **Corpo**. Geral mostra hora, status, duração, a rota, para onde foi (`http://localhost:3000/`) e os tamanhos. As colunas Hora e Duração somem da lista enquanto o painel está aberto.
2. **Andar pela lista.** Abra a aba **Cabeçalhos** e clique em outras linhas.
   - Esperado: o painel acompanha a linha e continua em Cabeçalhos.
3. **Valores ocultos.** Mande uma requisição com credencial:

   ```powershell
   curl.exe http://meuapp.sev/ -H "Authorization: Bearer segredo"
   ```

   - Esperado: em Cabeçalhos, `Authorization: ••••••••`. Marcar **Mostrar valores ocultos** mostra `Bearer segredo`.
4. **Sem corpos.** Com **Guardar corpos** desligado, mande um POST:

   ```powershell
   curl.exe -X POST http://meuapp.sev/api -H "Content-Type: application/json" -d '{"pedido":42}'
   ```

   - Esperado: a aba Corpo explica que os corpos não estão sendo guardados. **Repetir** diz que o corpo não foi guardado e não manda nada.
5. **Com corpos.** Ligue **Guardar corpos** e repita o POST.
   - Esperado: a ida mostra o JSON formatado, `"pedido": 42`. A volta mostra o que o servidor respondeu, se for texto.
6. **Copiar como curl.** Nessa linha, **Copiar como curl › Para PowerShell** e cole no terminal.
   - Esperado: o comando roda e aparece como linha nova no log, igual à original.
7. **Repetir.** Clique em **Repetir**.
   - Esperado: "Mandada de novo: …" com o status, e uma linha nova no topo da lista.
8. **Exportar HAR.** No **⋯** da aba, **Exportar HAR**, salve e arraste o arquivo para a aba Network do DevTools do Edge ou do Chrome.
   - Esperado: as requisições aparecem lá, com cabeçalhos e, nas que tinham, os corpos.
9. **Só memória.** Saia do Severino e abra de novo.
   - Esperado: o log começa vazio e **Guardar corpos** volta desligado.

## Parte 7. Avisos de saúde

Prova os critérios 1 a 6 da parte 7. Deixe a janela do Severino escondida (feche-a) para ver os avisos como eles aparecem no dia a dia.

1. **Ligar.** Edite a rota `meuapp.sev`, abra **Avançado** e marque **Avisar quando parar de responder**. Salve. Com o servidor da porta 3000 rodando, espere a rota aparecer como respondendo.
2. **Queda.** Pare o servidor (Ctrl+C) e espere 20 segundos.
   - Esperado: um aviso na bandeja, "meuapp.sev parou de responder", com "Nada responde em localhost:3000 há 15 segundos."
3. **Clique.** Clique no aviso.
   - Esperado: a janela abre na aba Rotas, filtrada por `meuapp.sev`.
4. **Volta.** Suba o servidor de novo.
   - Esperado: em até 10 segundos, "meuapp.sev voltou".
5. **Reinício rápido.** Pare e suba o servidor em menos de 10 segundos.
   - Esperado: nenhum aviso.
6. **Vários.** Marque a opção em mais uma rota ou serviço que use o mesmo servidor, e pare o servidor.
   - Esperado: um aviso só, "2 destinos pararam de responder", com os dois nomes.
7. **Pausado.** Pause o Severino pela bandeja e pare o servidor.
   - Esperado: nenhum aviso. Retome com o servidor parado: nenhum aviso também, porque a rota não respondeu desde que voltou.
8. **Nunca respondeu.** Saia do Severino com o servidor parado e abra de novo.
   - Esperado: nenhum aviso.
9. **Desligar tudo.** Em Configurações › Geral, desligue **Avisos de rotas e serviços**, suba e pare o servidor.
   - Esperado: nenhum aviso. Ligue de novo antes de seguir.

## Parte 5. Regras por rota

Pendente.

## Parte 6. Pasta como destino

Pendente.

## Parte 1. Ambientes

Pendente.

## Parte 3. Arquivo do projeto

Pendente.

## Parte 4. CA confiável no WSL

Pendente.
