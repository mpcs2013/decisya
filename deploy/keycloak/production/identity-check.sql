-- Read-only identity check for the deployable stack (issue #121, G2 D5, G3 G4-121-03 d and G4-121-05).
--
-- Run by `stackctl.py verify` and `realm rebuild` as:
--   docker compose exec -T postgres psql -U postgres -d keycloak -At -v ON_ERROR_STOP=1 < identity-check.sql
-- The role is the Postgres superuser, so "SELECT only" is not enough on its own: this file is one
-- read-only transaction holding one query (tests/Decisya.Identity.Tests asserts that, line by line).
--
-- Output: one `key=value` line per fact, the value an integer, `true` or `false`. It never
-- selects a username, an email address, an id or an attribute value, only counts and booleans, so
-- the output is safe to print.
--
-- master_users_without_otp counts every master-realm user, service accounts included: a master
-- client with a service account and an admin role is full admin power without OTP (G6 F-01).
--
-- The query is tied to Keycloak's table layout. A Keycloak upgrade runs the Integration test that
-- executes this file against a real Keycloak database, so a schema change fails CI, not the NAS.
BEGIN TRANSACTION READ ONLY;

SELECT t.line
FROM (VALUES
    (1, 'realm_present=' || (EXISTS (SELECT 1 FROM realm r WHERE r.name = 'decisya'))::text),
    (2, 'ssl_required_all=' || COALESCE((SELECT r.ssl_required = 'ALL' FROM realm r WHERE r.name = 'decisya'), false)::text),
    (3, 'events_enabled=' || COALESCE((SELECT r.events_enabled FROM realm r WHERE r.name = 'decisya'), false)::text),
    (4, 'admin_events_enabled=' || COALESCE((SELECT r.admin_events_enabled FROM realm r WHERE r.name = 'decisya'), false)::text),
    (5, 'admin_events_details_off=' || COALESCE((SELECT NOT r.admin_events_details_enabled FROM realm r WHERE r.name = 'decisya'), false)::text),
    (6, 'events_expiration_ok=' || COALESCE((SELECT r.events_expiration = 7776000 FROM realm r WHERE r.name = 'decisya'), false)::text),
    (7, 'admin_events_expiration_ok=' || (EXISTS (
        SELECT 1 FROM realm_attribute a JOIN realm r ON r.id = a.realm_id
        WHERE r.name = 'decisya' AND a.name = 'adminEventsExpiration' AND a.value = '7776000'))::text),
    (8, 'jboss_logging_listener=' || (EXISTS (
        SELECT 1 FROM realm_events_listeners l JOIN realm r ON r.id = l.realm_id
        WHERE r.name = 'decisya' AND l.value = 'jboss-logging'))::text),
    (9, 'browser_flow_ok=' || (EXISTS (
        SELECT 1 FROM realm r JOIN authentication_flow f ON f.id = r.browser_flow
        WHERE r.name = 'decisya' AND f.alias = 'decisya-browser'))::text),
    (10, 'level_2_admin_conditional=' || (EXISTS (
        SELECT 1 FROM authentication_flow f JOIN authentication_execution e ON e.auth_flow_id = f.id JOIN realm r ON r.id = f.realm_id
        WHERE r.name = 'decisya' AND f.alias = 'level-2-admin' AND e.requirement = 1))::text),
    (11, 'level_2_tenant_conditional=' || (EXISTS (
        SELECT 1 FROM authentication_flow f JOIN authentication_execution e ON e.auth_flow_id = f.id JOIN realm r ON r.id = f.realm_id
        WHERE r.name = 'decisya' AND f.alias = 'level-2-tenant' AND e.requirement = 1))::text),
    (12, 'password_policy_ok=' || COALESCE((
        SELECT string_to_array(r.password_policy, ' and ') @> ARRAY['length(12)', 'maxLength(128)', 'notUsername', 'notContainsUsername', 'notEmail', 'passwordHistory(3)', 'passwordBlacklist(common-passwords.txt)']
        FROM realm r WHERE r.name = 'decisya'), false)::text),
    (13, 'breached_list_in_policy=' || COALESCE((SELECT position('breached-passwords.txt' IN r.password_policy) > 0 FROM realm r WHERE r.name = 'decisya'), false)::text),
    (14, 'users_total=' || (SELECT count(*) FROM user_entity u JOIN realm r ON r.id = u.realm_id
        WHERE r.name = 'decisya' AND u.service_account_client_link IS NULL)::text),
    (15, 'users_without_synthetic=' || (SELECT count(*) FROM user_entity u JOIN realm r ON r.id = u.realm_id
        WHERE r.name = 'decisya' AND u.service_account_client_link IS NULL
          AND NOT EXISTS (SELECT 1 FROM user_attribute a WHERE a.user_id = u.id AND a.name = 'synthetic' AND a.value = 'true'))::text),
    (16, 'master_users_without_otp=' || (SELECT count(*) FROM user_entity u JOIN realm r ON r.id = u.realm_id
        WHERE r.name = 'master'
          AND NOT EXISTS (SELECT 1 FROM credential c WHERE c.user_id = u.id AND c.type = 'otp'))::text),
    (17, 'bff_secret_unresolved=' || (EXISTS (
        SELECT 1 FROM client c JOIN realm r ON r.id = c.realm_id
        WHERE r.name = 'decisya' AND c.client_id = 'decisya-bff' AND c.secret LIKE '%${%'))::text),
    (18, 'bff_secret_short=' || (EXISTS (
        SELECT 1 FROM client c JOIN realm r ON r.id = c.realm_id
        WHERE r.name = 'decisya' AND c.client_id = 'decisya-bff' AND (c.secret IS NULL OR length(c.secret) < 32)))::text),
    (19, 'bff_redirect_unresolved=' || (EXISTS (
        SELECT 1 FROM redirect_uris u JOIN client c ON c.id = u.client_id JOIN realm r ON r.id = c.realm_id
        WHERE r.name = 'decisya' AND c.client_id = 'decisya-bff' AND (u.value LIKE '%${%' OR u.value LIKE '%*%')))::text),
    (20, 'bff_logout_uris_unresolved=' || (EXISTS (
        SELECT 1 FROM client_attributes a JOIN client c ON c.id = a.client_id JOIN realm r ON r.id = c.realm_id
        WHERE r.name = 'decisya' AND c.client_id = 'decisya-bff'
          AND a.name IN ('post.logout.redirect.uris', 'backchannel.logout.url')
          AND (a.value LIKE '%${%' OR a.value LIKE '%*%')))::text)
) AS t(n, line)
ORDER BY t.n;

ROLLBACK;
