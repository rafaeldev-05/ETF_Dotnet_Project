# Requisitos — etapa 1

## Objetivo e escopo

Estudar o recebimento consistente de solicitações fictícias de compra limitada de ETFs. Um aceite cria uma ordem `PendingExecution`, não uma compra executada. Inclui catálogo, recebimento idempotente, reserva de saldo e consulta individual pelo proprietário.

Clientes e contas já existem. Cadastro, Pix, taxas, execução parcial, cancelamento, integrações e operações financeiras reais estão fora do escopo. Não há carteira de posições, cotação ao vivo, listagem paginada de ordens nem endpoint de saldo nesta fatia.

## Hipóteses explícitas

- Um cliente corresponde a uma conta. `ClientId` identifica essa conta; não modelamos uma tabela de cadastro que não participa do exercício.
- Dois clientes fictícios (Alice/Bob), cada um com 1.000,00 disponíveis e zero reservados. Dois ETFs fictícios sem relação com produtos reais.
- Uma única moeda fictícia com duas casas decimais. Quantidade inteira entre 1 e 2.147.483.647. Preço e reserva positivos até `9999999999999999.99`.
- C# `decimal` e PostgreSQL `numeric(18,2)`. Multiplicação por inteiro é exata dentro dos limites. Não há arredondamento comercial nesta etapa: valor com fração de centavo é inválido. O teste com `decimal.Round` apenas valida se haveria perda de informação; não muda o preço aceito.
- Ordem limitada não verifica preço de mercado; o limite será condição para uma futura execução simulada.
- Saldo total (`Available + Reserved`) permanece constante; a reserva apenas transfere valor entre essas duas parcelas. Não há depósito, saque ou consumo da reserva ainda.
- Ordens aceitas e suas chaves não expiram nem são removidas nesta etapa. Rejeições não consomem a chave.
- Catálogo público; ordens exigem identidade demo explícita. A aplicação só inicia em Development até existir autenticação real.

## Regras e contrato

1. `GET /etfs` devolve os ETFs cadastrados, ordenados por símbolo.
2. `POST /orders` recebe `etf`, `quantity`, `limitPrice`, `Idempotency-Key` e identidade demo pelo cabeçalho.
3. ETF é normalizado por remoção de espaços externos e conversão para maiúsculas; entrada possui no máximo 16 caracteres antes da normalização.
4. Chave contém de 1 a 128 caracteres ASCII visíveis, sem espaços. É opaca e sensível a maiúsculas; não é aparada. Seu escopo é o cliente.
5. Conteúdo idempotente é a tupla (ETF normalizado, quantidade, preço decimal). Ordem das propriedades JSON e zeros decimais finais não fazem diferença. Propriedades desconhecidas são ignoradas e não fazem parte do comando.
6. Mesma chave e conteúdo válido: mesma ordem, timestamp, valores e Location, inclusive após esgotar saldo. Números JSON podem ter zeros finais distintos, mantendo o mesmo valor.
7. Mesma chave e conteúdo válido diferente: 409. Conteúdo estruturalmente inválido é rejeitado com 400 antes da busca da chave.
8. Validar ETF existente e saldo suficiente; reservar e inserir ordem numa única transação. Nenhuma requisição concorrente pode reutilizar saldo já reservado.
9. Só retornar 201 após commit confirmado. `Location` aponta para `/orders/{id}` e o corpo informa `PendingExecution`.
10. Consulta inclui `ClientId` no filtro. Ordem de outro cliente e ordem ausente retornam 404, evitando revelar sua existência.

## Metas futuras, não resultados

| Dimensão | Referência a validar |
|---|---|
| Solicitações de compra | 100/s sustentadas |
| Pico de solicitações | 1.000/s |
| Consultas | 3.000/s |
| Latência de recebimento | p95 até 500 ms |

Ainda faltam duração dos picos, número de contas ativas, concentração por conta, mistura de leitura/escrita, volume histórico, distribuição de payloads, hardware e orçamento de erros. Um benchmark futuro deverá declarar esses fatores e medir p95/p99, erros, espera de locks, pool de conexões e utilização do banco. Os testes atuais validam correção, não capacidade.

## Critérios de aceite e limites

Testes com PostgreSQL real cobrem reservas, acesso, idempotência concorrente e rollback após gravação. Não afirmamos tolerância a falha de infraestrutura, recuperação de desastre ou autenticação de produção. O banco único é dependência de disponibilidade; uma conta muito disputada pode ter latência alta.
