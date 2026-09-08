# Arquitetura — monólito modular inicial

## Componentes e dependências

Um processo ASP.NET Core e um PostgreSQL. Os módulos funcionais iniciais são catálogo e ordens/contas; nesta fatia pequena compartilham projetos por responsabilidade e um contexto de persistência. Não há isolamento físico por módulo nem repositório genérico.

| Projeto | Responsabilidade |
|---|---|
| `Etf.Domain` | Entidades, estado da ordem, precisão monetária e regra de reserva da conta; não referencia EF ou ASP.NET |
| `Etf.Application` | Contrato de entrada/saída, validação do comando, caso de uso `Orders` e porta `IOrderStore` |
| `Etf.Infrastructure` | Adaptador PostgreSQL, EF Core, mapeamentos, migrations, seed explícito e implementação da operação atômica |
| `Etf.Api` | HTTP, identidade demo, Problem Details e composição de dependências |
| `Etf.Tests` | Integração HTTP e transacional com bancos PostgreSQL temporários |

Dependências de compilação: Application → Domain; Infrastructure → Application; API → Infrastructure para registrar serviços. API usa os contratos de Application. Não existe referência de Domain/Application para Infrastructure. `IOrderStore` é uma porta específica com três operações; seu método de escrita promete reserva + criação atômicas, evitando expor transações EF ao caso de uso. A decisão de HTTP está na API, mas o pequeno tipo de erro de aplicação carrega o status correspondente como simplificação explícita desta etapa.

## Fluxo de criação

1. API obtém a identidade demo e o cabeçalho de idempotência.
2. Aplicação valida formato, limites monetários e normaliza ETF.
3. Adaptador abre transação PostgreSQL `READ COMMITTED`.
4. Executa `SELECT ... FOR UPDATE` na conta por ID, com parâmetro SQL.
5. Com a conta bloqueada, consulta `(ClientId, IdempotencyKey)`. Se já existe, compara os três campos; devolve a ordem ou sinaliza conflito. A idempotência precede a nova verificação de saldo.
6. Verifica ETF cadastrado, calcula reserva e pede à conta `TryReserve`. A regra de transferência de saldo está no domínio.
7. Adiciona a ordem `PendingExecution`, salva e confirma a transação.
8. API devolve `201 Created` com `Location`; nada chama uma corretora ou executa a compra.

Apenas a persistência coordena a seção crítica porque as leituras precisam acontecer sob o bloqueio do banco. As regras de entrada e de saldo ficam fora do EF. Não há `lock` C# ou estado de idempotência em memória.

## Por que a concorrência está protegida

O bloqueio dura até o fim da transação. Uma segunda solicitação da mesma conta aguarda e depois lê o saldo atualizado. A consulta seguinte, em READ COMMITTED, vê a ordem que a primeira confirmou. Assim, tanto a mesma chave quanto chaves diferentes são serializadas por conta. Contas diferentes não disputam essa linha. Esse comportamento é o descrito pela [documentação de bloqueios do PostgreSQL](https://www.postgresql.org/docs/17/explicit-locking.html).

Há também índice único `(ClientId, IdempotencyKey)`, CHECK de saldos não negativos, CHECK de quantidade/preço/reserva/estado e chaves estrangeiras para conta e ETF. O índice é uma proteção adicional contra duplicatas; sozinho não protege a leitura e atualização do saldo. O CHECK sozinho também não impediria uma atualização perdida que sobrescrevesse um saldo positivo antigo. Todos os futuros caminhos que alterem saldo devem respeitar o mesmo protocolo transacional.

As garantias não dependem de uma única instância da API: o coordenador é o PostgreSQL. Múltiplas instâncias não foram exercitadas como topologia neste teste; conexões e contextos distintos concorrem contra o banco real.

## Atomicidade, falhas e precisão

Atualização da conta, inserção da ordem e chave idempotente ficam na mesma transação. Uma exceção antes do commit descarta tudo ao liberar a transação. Não existe reserva persistida sem ordem nesse caminho. `SaveChanges` faz parte da transação explícita, conforme a [documentação EF Core](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

Se o commit ocorrer e a resposta HTTP se perder, o cliente deve repetir a mesma chave e conteúdo. Se a conexão cair durante o commit, o servidor pode desconhecer o resultado: não se deve supor rollback nem gerar outra chave. A repetição consulta a verdade persistida. Não há promessa de entrega “exactly once”; há uma única criação/reserva por chave persistida, enquanto preservarmos esse histórico.

Dinheiro usa `decimal`/`numeric(18,2)`, sem `double`. Valores incompatíveis são rejeitados antes do banco, que de outro modo poderia arredondar. Não há taxas nem divisão que exija política de arredondamento adicional. Timestamps são UTC truncados para microssegundos antes de salvar, alinhando a precisão ao PostgreSQL e preservando o instante em criação e replay.

## Trade-offs para defender na entrevista

- **Monólito:** mantém uma única transação e implantação simples enquanto o domínio ainda é pequeno. Separar responsabilidades não exige distribuição de processos.
- **PostgreSQL:** transações, constraints e bloqueio por linha resolvem o problema de consistência diretamente. EF facilita persistência/migrations; um SQL localizado expressa o bloqueio específico necessário.
- **Bloqueio pessimista por conta:** o raciocínio é simples e não exige loop de retries otimistas. O custo é fila para uma conta disputada, inclusive em replays. Não fazemos chamada externa dentro da transação.
- **Comparação de conteúdo:** guardar os três campos da própria ordem evita tabela de idempotência e hash desnecessários. Caso o contrato cresça, sua canonicalização precisará ser versionada.
- **201:** a ordem foi criada, mas continua aguardando execução. O sucesso não significa aquisição de cotas.
- **404 por proprietário:** a consulta filtra ID e cliente no banco; não carrega qualquer ordem para depois confiar num filtro de apresentação.
- **Identidade demo restrita:** é conveniente para estudar propriedade, mas não prova a identidade do usuário. O bloqueio de startup fora de Development evita confundir esse mecanismo com autenticação real.

## Excalidraw: desenho simples para reproduzir

Desenhe “Cliente fictício” à esquerda e um retângulo grande “Monólito ASP.NET Core” ao centro. Dentro dele, três caixas em sequência: “API / identidade demo” → “Aplicação / criar ordem” → “Infra / EF Core”. Abaixo da aplicação, uma caixa “Domínio / conta, ordem, dinheiro”, usada pela aplicação e infraestrutura.

À direita, desenhe o banco assim:

```text
API → PostgreSQL
          ├── Orders
          ├── Accounts
          └── OutboxMessages

Publisher → Broker em memória
Broker → Consumer → PostgreSQL
```

Ligue `API` ao PostgreSQL com a seta “transação: lock conta → idempotência → reserva + ordem + outbox → commit”. Aponte Publisher para “lote pending; publica fora da transação; marca Published depois”. Aponte Consumer para “deduplica EventId; lock conta → ordem; executa ou rejeita; grava ProcessedEvents”. Escreva “at-least-once” entre Broker e Consumer e “contas distintas em paralelo” junto ao lock da conta.

## Evolução futura — ainda não implementada

Nesta etapa, a outbox, publisher, broker em memória e consumidor idempotente estão implementados no mesmo monólito para estudo. O broker transporta o contrato `OrderCreatedV1`; o simulador decide deterministicamente o resultado. A publicação pode duplicar dentro da janela “publicou, caiu antes de marcar Published”, e `ProcessedEvents` absorve a duplicata.

Uma evolução futura substituirá o broker local por um broker externo e moverá publisher/consumer para processos implantáveis separadamente. Outbox, broker e banco continuarão sem transação distribuída; retries, backoff, observabilidade e DLQ operacional precisarão de políticas reais. Kubernetes, Kafka e RabbitMQ não fazem parte desta etapa.

Autenticação real e testes de carga ficam para evolução deliberada. O broker local não é um serviço implantável e não representa disponibilidade ou durabilidade de produção.
