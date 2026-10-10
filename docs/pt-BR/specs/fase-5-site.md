# Spec: Fase 5, Site e publicação

**Status:** em implementação desde 2026-10-10, com as decisões abaixo no padrão
**Base:** pedido de 2026-10-10: "a fase 5 vai ser a criação do site do Severino, pipeline do GitHub Actions para publicar o site, e pipeline de build para gerar versão e release com .exe anexado na release".
**Pré-requisito:** nenhum; corre em paralelo ao checklist da Fase 4.

## Objetivo

Quem ouve falar do Severino encontra uma página que explica o que ele faz e baixa o instalador da versão mais recente com um clique. Publicar uma versão nova é um clique no GitHub, sem build na máquina de ninguém.

## Critérios de pronto

1. **Site no ar.** A página de apresentação abre em `https://lucassm02.github.io/severino/`, no celular e no desktop.
2. **Site sempre atual.** Um push na `main` que muda `site/` publica a página sozinho.
3. **Release com instalador.** Rodar o workflow **Release** gera a próxima versão, roda os testes, monta o instalador e publica a release `vX.Y.Z` com `Severino-Setup-X.Y.Z.exe` anexado e as notas geradas pelos commits.
4. **Download aponta para a última versão.** Os botões do site baixam o `.exe` da release mais recente e mostram a versão e o tamanho. Sem release, levam à página de releases.

## A página

Em `site/`, HTML e CSS estáticos, sem build, com um script pequeno.

- **Conceito:** o Severino é o porteiro. A peça central é o quadro de moradores da portaria, de feltro e letras de plástico, listando domínios e portas como apartamentos. O resto da página fica sóbrio.
- **Cores e tipos:** as do mascote (farda `#011D41`, quepe `#013166`, laranja `#EE8F3B`); Bricolage Grotesque nos títulos e no quadro, Source Sans 3 no texto.
- **Seções:** abertura com o quadro; como funciona, em três passos; a janela do app; serviços do Kubernetes, Docker e WSL; o que o Severino mexe na máquina; download.
- **Capturas:** da janela real do app, com dados de exemplo, geradas offscreen. Nada de mock desenhado à mão para a janela.
- **Movimento:** só as letras do quadro sendo encaixadas ao carregar, e nada com `prefers-reduced-motion`.
- **Prévia local:** `python -m http.server 8765 --directory site`.

## Pipelines

- **`.github/workflows/site.yml`:** push na `main` com mudança em `site/` (ou manual) publica a pasta no GitHub Pages pelo próprio Actions.
- **`.github/workflows/release.yml`:** manual, em Actions › Release, escolhendo o que sobe: `patch`, `minor` ou `major`.
  - A próxima versão sai da última tag `v*`. Antes da primeira release, parte da versão do `Directory.Build.props` (hoje 0.3.0).
  - Num runner Windows: `dotnet test`, Inno Setup pelo Chocolatey e `scripts/build-installer.ps1 -Version X.Y.Z`.
  - `gh release create vX.Y.Z` cria a tag no commit da execução, anexa o instalador e gera as notas.

## Configuração que só o dono do repositório faz

- Settings › Pages › Source: **GitHub Actions**. Sem isso, o workflow do site falha no deploy.
- O GitHub Pages em repositório privado exige um plano pago; em repositório público é grátis.

## Decisões a confirmar

1. **Hospedagem no GitHub Pages**, em `lucassm02.github.io/severino`. *Padrão: sim.* Um domínio próprio entra depois, com um arquivo `CNAME`.
2. **Versão escolhida no disparo** (`patch`, `minor`, `major`), não calculada dos Conventional Commits. *Padrão: sim.* É mais previsível; calcular pelos commits pode vir depois.
3. **Testes antes de publicar.** *Padrão: sim.* Uma falha impede a release.
4. **Instalador sem assinatura digital.** *Padrão: sim, por enquanto.* O site avisa sobre o SmartScreen. Assinar exige um certificado de code signing pago.
5. **Site só em português.** *Padrão: sim.*

## Fora do escopo

- Documentação completa no site; o guia de uso continua no app e nos roteiros.
- Atualização automática do app a partir das releases.
- Assinatura do instalador.
