# Guia de testes: Fase 7, Aviso de versão nova

Roteiro manual para fechar a Fase 7. Cobre os critérios de pronto do [spec](../specs/fase-7-atualizacoes.md) que os testes automatizados não alcançam: a consulta ao GitHub de verdade, o aviso na janela e na bandeja, e a instalação por cima.

Já verificado no desenvolvimento, em 2026-10-10:
- a consulta real ao GitHub, fingindo a versão 0.3.0, achou a v0.4.0;
- o download do instalador da v0.4.0 (81 MB) passou na conferência do SHA-256.

## Convenções

- Os comandos são para **PowerShell**, num terminal comum, a não ser quando o passo pedir administrador.
- Para fechar o Severino, use **Sair** no ícone da bandeja. Fechar a janela só esconde o app.
- A consulta automática só existe no build Release, que é o do instalador. Em `dotnet run`, use **Procurar agora**.

## 0. Preparação

Instale uma versão mais antiga que a última release, para o app ter o que oferecer:

```powershell
./scripts/build-installer.ps1 -Version 0.3.9
```

Rode `artifacts\installer\Severino-Setup-0.3.9.exe` e instale por cima do que estiver instalado. Configurações › Sobre deve mostrar **Severino 0.3.9**.

Antes de seguir, crie uma rota qualquer, como `meuapp.sev → localhost:3000`, para conferir que ela sobrevive à atualização.

## 1. O aviso aparece

Prova o critério 1.

1. Saia do Severino e abra de novo pelo menu Iniciar.
2. Espere até um minuto.

Esperado:
- No topo da janela aparece **Severino 0.4.0 saiu**, com "Você está na 0.3.9", e os botões **Atualizar agora**, **Ver novidades** e **Pular esta versão**.
- Em Configurações › Sobre: "A versão 0.4.0 saiu. Você está na 0.3.9.", a hora da consulta e o botão **Atualizar agora**.

## 2. A bandeja avisa uma vez

Prova o critério 2.

1. Em Configurações › Manutenção, abra a pasta de configuração e, com o app fechado, apague a linha `"notifiedVersion"` do `config.json`.
2. Abra o Severino e feche a janela logo em seguida (ele fica na bandeja).
3. Espere até um minuto.

Esperado:
- Uma notificação **Severino 0.4.0 saiu**.
- Saindo e abrindo de novo, com a janela escondida, ela não se repete.

## 3. Ver novidades e pular

Prova o critério 5.

1. **Ver novidades** abre a página da release v0.4.0 no navegador.
2. O **X** do aviso o esconde. Configurações › Sobre continua oferecendo **Atualizar agora**.
3. Saia e abra de novo: o aviso volta.
4. **Pular esta versão**: o aviso some. Saia e abra de novo: ele não volta.
5. Em Configurações › Sobre, **Procurar agora** mostra a 0.4.0 de novo, porque quem pergunta vê a versão pulada.

## 4. Desligar a consulta

Prova o critério 5.

1. Desligue **Procurar versões novas** em Configurações › Sobre.
2. Saia, abra de novo e espere um minuto.

Esperado: nenhum aviso. **Procurar agora** continua funcionando. Religue a opção no fim.

## 5. Sem internet

Prova o critério 7.

1. Desligue a rede (modo avião, ou desconecte o cabo e o Wi-Fi).
2. Saia, abra de novo e espere um minuto.

Esperado:
- Nenhum aviso e nenhum erro.
- **Procurar agora** diz "Não deu para consultar o GitHub: …".

Religue a rede no fim.

## 6. Atualizar pelo app

Prova os critérios 3 e 4.

1. No aviso, clique em **Atualizar agora**. A confirmação diz que o app baixa a versão 0.4.0 (77 MB), fecha, instala e abre de novo.
2. Confirme.

Esperado:
- A barra de progresso anda até o fim.
- O app fecha e o Windows pede o UAC. Aceite.
- O instalador roda só com a barra de progresso, sem perguntas.
- Configurações › Sobre mostra **Severino 0.4.0**, e a rota `meuapp.sev` continua lá, respondendo.

Sobre o app abrir sozinho no fim: o instalador da v0.4.0 é anterior a esta fase e não sabe fazer isso, então abra o Severino pelo menu Iniciar. Quem atualizar para a próxima release vê o app abrir sozinho. Para conferir isso já, rode o instalador desta fase como o app faria:

```powershell
./scripts/build-installer.ps1 -Version 0.4.1
artifacts\installer\Severino-Setup-0.4.1.exe /SILENT /SP- /NORESTART /RELAUNCH=1
```

Esperado: UAC, a barra de progresso, e o Severino abre sozinho na 0.4.1, como o seu usuário (o ícone aparece na bandeja).

Depois, para voltar à versão publicada, instale a da release por cima.

## Resultado

| Teste | Critério | Resultado |
|---|---|---|
| 1. O aviso aparece | 1, 6 | |
| 2. A bandeja avisa uma vez | 2 | |
| 3. Ver novidades e pular | 5 | |
| 4. Desligar a consulta | 5 | |
| 5. Sem internet | 7 | |
| 6. Atualizar pelo app | 3, 4 | |
