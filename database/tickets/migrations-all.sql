DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'tickets') THEN
        CREATE SCHEMA tickets;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS tickets.__ef_migrations_history (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'tickets') THEN
            CREATE SCHEMA tickets;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE TABLE tickets.outbox_event (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        aggregate_type character varying(50) NOT NULL,
        aggregate_public_id character varying(40) NOT NULL,
        aggregate_version bigint NOT NULL,
        event_type character varying(100) NOT NULL,
        payload jsonb NOT NULL,
        occurred_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_outbox_event PRIMARY KEY (id),
        CONSTRAINT ak_outbox_event_tenant_id_id UNIQUE (tenant_id, id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE TABLE tickets.ticket (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        public_id character varying(34) NOT NULL,
        title character varying(200) NOT NULL,
        description character varying(4000),
        status character varying(20) NOT NULL,
        assignee_employee_id uuid,
        assignee_display_name character varying(201),
        version bigint NOT NULL,
        created_at timestamp with time zone NOT NULL,
        closed_at timestamp with time zone,
        CONSTRAINT pk_ticket PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE TABLE tickets.outbox_message (
        id uuid NOT NULL,
        tenant_id integer NOT NULL,
        event_id uuid,
        transport character varying(20) NOT NULL,
        destination character varying(320) NOT NULL,
        payload jsonb NOT NULL,
        status character varying(20) NOT NULL,
        attempts integer NOT NULL,
        next_attempt_at timestamp with time zone NOT NULL,
        locked_until timestamp with time zone,
        completed_at timestamp with time zone,
        last_error character varying(1000),
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_outbox_message PRIMARY KEY (id),
        CONSTRAINT fk_outbox_message_outbox_event_tenant_id_event_id FOREIGN KEY (tenant_id, event_id) REFERENCES tickets.outbox_event (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE INDEX ix_outbox_event_tenant_id ON tickets.outbox_event (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE UNIQUE INDEX ix_outbox_event_tenant_id_aggregate_type_aggregate_public_id_a ON tickets.outbox_event (tenant_id, aggregate_type, aggregate_public_id, aggregate_version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE INDEX ix_outbox_message_status_next_attempt_at ON tickets.outbox_message (status, next_attempt_at) WHERE status = 'Pending';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE INDEX ix_outbox_message_tenant_id ON tickets.outbox_message (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE INDEX ix_outbox_message_tenant_id_event_id ON tickets.outbox_message (tenant_id, event_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE UNIQUE INDEX ix_ticket_public_id ON tickets.ticket (public_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE INDEX ix_ticket_tenant_id ON tickets.ticket (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    CREATE INDEX ix_ticket_tenant_id_status ON tickets.ticket (tenant_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925033813_InitialTickets') THEN
    INSERT INTO tickets.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260925033813_InitialTickets', '10.0.10');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tickets.__ef_migrations_history WHERE "migration_id" = '20260925043445_AddTicketConcurrencyToken') THEN
    INSERT INTO tickets.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260925043445_AddTicketConcurrencyToken', '10.0.10');
    END IF;
END $EF$;
COMMIT;

