# Solicitações fictícias de compra de ETFs

Projeto de estudo de System Design em C#/.NET. Receber uma ordem **não executa uma compra**: o backend reserva saldo e persiste `PendingExecution`. Nenhuma operação financeira real é realizada.

## Ambiente e preparação

Inspeção em 08/09/2026: pasta inicialmente vazia, sem instruções locais `AGENTS.md`. macOS Apple Silicon (`arm64`), Git, VS Code e Docker já instalados. O Docker Desktop estava parado e foi iniciado. SDK **10.0.400**, runtime **10.0.11**, instalados localmente em `.dotnet/`, sem alterar o PATH global. O SDK é ignorado pelo Git.

.NET 10 é LTS ativo com suporte até novembro de 2028, segundo a [política oficial da Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy). Usamos a distribuição ARM64 conforme a [instalação oficial para macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos). EF Core 10.0.11 e Npgsql EF 10.0.3. `System.Security.Cryptography.Xml` está explicitamente atualizado porque a ferramenta de design EF trazia uma dependência transitiva vulnerável.

Se o SDK local já existe, basta carregar o ambiente. Em outra máquina macOS ARM64, prepare-o primeiro:

```sh
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/etf-dotnet-install.sh
bash /tmp/etf-dotnet-install.sh --version 10.0.400 --architecture arm64 --install-dir "$PWD/.dotnet" --no-path
```

Na raiz do projeto, em cada terminal:

```sh
source scripts/env.sh
dotnet --info
docker info
```

Se `docker info` falhar, abra o Docker Desktop e aguarde o daemon iniciar. A primeira restauração exige acesso à Microsoft/NuGet e o primeiro `compose up` exige acesso ao registro de imagens. Em ambientes restritos, sockets locais e downloads podem exigir permissão; não há instalação global nem necessidade de `sudo` para o SDK local.

## Executar

```sh
source scripts/env.sh
docker compose up -d --wait
dotnet tool restore
dotnet restore
dotnet build --no-restore -m:1
dotnet ef database update --project src/Etf.Infrastructure --startup-project src/Etf.Api
dotnet run --project src/Etf.Api --no-build -- --seed-demo
dotnet run --project src/Etf.Api --no-build -- --urls http://localhost:5080
```

O PostgreSQL 17 fica em `127.0.0.1:55432`, banco `etf`, usuário `etf`, senha fictícia `etf_demo_only`. O volume mantém os dados entre reinícios. Para parar preservando os dados: `docker compose stop`. Não execute remoção de volumes se quiser preservar as ordens de estudo.

Migrations são explícitas; a API não altera o esquema ao iniciar. O seed só insere registros ausentes e não repõe saldo gasto. Execute-o após a migration. A aplicação inteira recusa iniciar fora de `Development`, inclusive o seed. Para usar outro PostgreSQL, sobrescreva `ConnectionStrings__Trading` depois de carregar o ambiente.

## Identidade de demonstração

Cada cliente tem exatamente uma conta previamente cadastrada, saldo disponível inicial de **1.000,00 unidades monetárias fictícias**, reservado zero:

| Cliente | `X-Demo-Client-Id` |
|---|---|
| Alice | `11111111-1111-1111-1111-111111111111` |
| Bob | `22222222-2222-2222-2222-222222222222` |

O cabeçalho seleciona uma identidade de teste; **não é autenticação de produção**. Qualquer pessoa com acesso ao servidor Development pode escolher esses clientes. Não exponha esse servidor de demonstração. Cabeçalho ausente, múltiplo, inválido ou cliente não permitido retorna 401 nas rotas de ordens. O catálogo é público. Nenhum cliente vem do corpo da ordem.

## Exemplos

Consultar catálogo fictício (`DEMO11`, `TEST11`):

```sh
curl -i http://localhost:5080/etfs
```

Criar ordem de Alice, reservando 251,00:

```sh
curl -i http://localhost:5080/orders \
  -H 'Content-Type: application/json' \
  -H 'X-Demo-Client-Id: 11111111-1111-1111-1111-111111111111' \
  -H 'Idempotency-Key: estudo-001' \
  -d '{"etf":"DEMO11","quantity":2,"limitPrice":125.50}'
```

Resposta: `201 Created`, `Location: /orders/<id>` e corpo com `id`, `etf`, `quantity`, `limitPrice`, `reservation`, `status: "PendingExecution"` e `createdAt`. Não existe execução automática nesta etapa.

Consultar usando o ID retornado:

```sh
curl -i http://localhost:5080/orders/COLE-O-ID-AQUI \
  -H 'X-Demo-Client-Id: 11111111-1111-1111-1111-111111111111'
```

Repita o POST sem alterar a chave ou conteúdo: retorna a mesma ordem, `201` e o mesmo `Location`, sem nova reserva. Altere `quantity` para 3 mantendo a chave: retorna 409. Troque a identidade da consulta para Bob: retorna 404, como se a ordem não existisse.

## Contrato e erros

`quantity` é inteiro positivo de 32 bits. `limitPrice` é número decimal positivo com até duas casas significativas após a vírgula; no JSON use ponto. Reserva = quantidade × preço limite. Armazenamento `numeric(18,2)`; máximo monetário `9999999999999999.99`. Frações de centavo são rejeitadas, não arredondadas. `100`, `100.0` e `100.00` representam o mesmo preço. Clientes devem usar aritmética decimal, evitando a multiplicação de dinheiro em ponto flutuante.

Erros usam `application/problem+json`, com `status`, `title`, `detail` e, nos erros tratados de negócio, `code` estável:

| Situação | HTTP | Código |
|---|---|---|
| Quantidade/preço/ETF inválidos, reserva fora do limite | 400 | `invalid_order` |
| Chave ausente, múltipla ou inválida | 400 | `invalid_idempotency_key` |
| JSON ou tipos inválidos | 400 | `invalid_request` |
| Identidade demo inválida | 401 | `invalid_demo_identity` |
| Conta permitida mas ausente no banco | 401 | `unknown_client` |
| ETF não cadastrado | 404 | `etf_not_found` |
| Ordem ausente ou de outro cliente | 404 | `order_not_found` |
| Saldo insuficiente | 409 | `insufficient_balance` |
| Chave reutilizada com outro conteúdo válido | 409 | `idempotency_conflict` |

Falhas inesperadas retornam Problem Details 500; o cliente pode repetir usando a mesma chave para descobrir o resultado persistido. Erros de roteamento (por exemplo ID que não é GUID) usam o tratamento padrão de status HTTP, sem código de negócio. As validações de formato precedem a consulta de idempotência.

## Migrations

A migration `InitialOrders` e o snapshot estão em `src/Etf.Infrastructure/Migrations`. Criação de uma próxima migration, quando houver mudança de modelo:

```sh
dotnet ef migrations add NomeDaMudanca --project src/Etf.Infrastructure --startup-project src/Etf.Api
dotnet ef database update --project src/Etf.Infrastructure --startup-project src/Etf.Api
dotnet ef migrations has-pending-model-changes --project src/Etf.Infrastructure --startup-project src/Etf.Api
```

## Testes com PostgreSQL real

```sh
source scripts/env.sh
docker compose up -d --wait
dotnet test -m:1 --logger 'trx;LogFileName=integration.trx' --results-directory TestResults
```

Cada caso cria um banco `etf_test_<guid>`, aplica as migrations, insere os dados fictícios e remove **somente seu banco temporário** ao terminar. O usuário de teste precisa poder criar/remover bancos e visualizar suas sessões. A conexão administrativa padrão usa o mesmo servidor local, banco `postgres`. Para outro servidor de teste:

```sh
export ETF_TEST_ADMIN='Host=localhost;Port=55432;Database=postgres;Username=etf;Password=etf_demo_only'
```

Use somente uma instância de teste. Não se usa EF InMemory; a ausência de PostgreSQL faz a suíte falhar, não pular testes silenciosamente. Os testes HTTP usam `WebApplicationFactory` e o banco real. As disputas esperam observar pelo menos duas sessões bloqueadas em `pg_stat_activity`, antes de liberar a conta. O teste de atomicidade injeta falha depois de `SaveChanges`, antes do commit, e verifica o banco por uma nova conexão/contexto.

Cobertura: criação/Location/consulta, reserva, saldo insuficiente, validações, chave obrigatória, ETF inexistente, replay após esgotar saldo, conflitos em cada campo, isolamento entre clientes, concorrência com chaves iguais e distintas, conteúdo concorrente conflitante, rollback e bloqueio de execução em Production. Consulte [o registro de validação](docs/validacao.md) para o resultado efetivamente executado.
