# Guia de testes: Fase 9, Inglês e português

Roteiro manual para fechar a Fase 9. Cobre os critérios de pronto do [spec](../specs/fase-9-idiomas.md) que os testes automatizados não alcançam: o idioma de verdade no Windows, no instalador, no navegador e no site.

Já verificado no desenvolvimento, em 2026-10-10:
- Testes automatizados:
  - as chaves e os placeholders batem entre os dois idiomas nos quatro projetos;
  - nenhum texto em português sobrou fora dos `.resx`;
  - um idioma sem tradução cai no inglês.
- Capturas das abas e das Configurações nos dois idiomas, sem texto cortado.
- O instalador compila com os dois idiomas e leva as traduções do app na pasta `pt-BR`.
- O site em inglês na raiz e em português em `/pt-BR/`, no desktop e no celular, com o vídeo de cada idioma.

## Convenções

- Os comandos são para **PowerShell**, num terminal comum.
- Para fechar o Severino, use **Sair** (ou **Exit**) no ícone da bandeja. Fechar a janela só esconde o app.

## 0. Preparação

```powershell
dotnet build
dotnet test
node scripts/check-links.mjs
```

Esperado:
- O build termina sem avisos.
- Todos os testes passam.
- O script termina com "no broken links".

## 1. Segue o Windows

Prova o critério 3.

1. Com o Windows em português, apague a linha `"language"` do `config.json`, se existir.
2. Abra o Severino. Esperado: tudo em português, como antes.
3. Em **Configurações › Geral › Idioma**, escolha **English**.
   - Esperado: aparece "O idioma troca quando o Severino abre de novo" e o botão **Reiniciar agora**.
4. Clique em **Reiniciar agora**.
   - Esperado: o app fecha e abre de novo sozinho, em inglês, já na aba **Settings**.
   - As rotas continuam respondendo.

## 2. Inglês por inteiro

Prova o critério 4. Com o app em inglês, passe por:

- as abas **Routes**, **Services**, **DNS**, **Requests** e **Settings**;
- **New route**, com um nome que exista na internet e com a porta 5432;
- **Import services**, **New service** e **New entry** (DNS);
- o menu `...` de uma rota e o de uma linha do hosts de fora;
- o menu do ícone da bandeja;
- em **Settings › About**, o botão **Check now**;
- **Review the setup check**, que abre o assistente de primeiro uso.

Esperado: nenhuma palavra em português. Nomes de exemplo como `pedidos.loja` não contam.

## 3. Páginas de erro do proxy

Prova o critério 4.

1. Com o app em inglês, abra `http://naoexiste.sev` no navegador.
   - Esperado: a página "Route not found", em inglês.
2. Desligue uma rota e abra o endereço dela. Esperado: "Route switched off".
3. Pare o servidor de uma rota e abra o endereço dela. Esperado: "Target is down".
4. Volte para **Português** e repita o passo 1. Esperado: "Rota não encontrada".

## 4. Volta ao Windows

Prova o critério 3.

1. Escolha **Igual ao Windows** e reinicie.
   - Esperado: português, se o Windows estiver em português.
2. Se tiver uma máquina ou uma conta com o Windows em inglês, instale lá. Esperado: tudo em inglês, sem escolher nada.

## 5. Instalador

Prova o critério 5.

```powershell
./scripts/build-installer.ps1 -Version 0.4.1
```

1. Rode `artifacts\installer\Severino-Setup-0.4.1.exe`. Esperado: as telas no idioma do Windows, sem perguntar o idioma.
2. Desinstale pelo Painel de Controle. Esperado: as perguntas da desinstalação no mesmo idioma ("Apagar também as suas rotas…" ou "Also delete your routes…").

## 6. Site

Prova o critério 6 e a decisão 6.

```powershell
python -m http.server 8765 --directory site
```

1. Abra <http://localhost:8765/>.
   - Esperado: o site em inglês.
   - O vídeo da seção "A shift at the front desk" é o em inglês e toca sozinho.
   - **Português**, no topo, leva a `/pt-BR/`.
2. Em `/pt-BR/`:
   - Esperado: tudo como antes, em português.
   - **English** leva de volta à raiz.
3. Abra <http://localhost:8765/naoexiste>. Esperado: a página 404, em inglês, com o link em português embaixo.

## 7. Repositório

Prova os critérios 1, 2 e 6.

1. No GitHub, o README da raiz está em inglês.
   - **Português**, no topo, leva a `docs/pt-BR/README.md`.
   - **English**, no topo desse, volta.
2. Na próxima release, as notas saem do CHANGELOG em inglês e terminam com o link "Em português".

## Resultado

| Teste | Critério | Resultado |
|---|---|---|
| 1. Segue o Windows | 3 | |
| 2. Inglês por inteiro | 4 | |
| 3. Páginas de erro do proxy | 4 | |
| 4. Volta ao Windows | 3 | |
| 5. Instalador | 5 | |
| 6. Site | 6 | |
| 7. Repositório | 1, 2, 6 | |
