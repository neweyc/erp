-- Run by ops/restore.sh INSIDE the restore's own transaction, after the restored data and before
-- COMMIT, so no committed copy ever has working restored sessions. That is also why this file has
-- no BEGIN or COMMIT of its own. NEVER run it against a live database: it signs out every user
-- and every operator.
--
-- A restore returns identity.session to the backup point. Any session revoked AFTER that point
-- is live again in the restored rows: a sign-out, the sessions of a user deactivated since, an
-- operator's revocation of a stolen session. The cookie carrying it may still be in someone's
-- browser, and it would work again. Revocation exists so those take effect immediately
-- (docs/auth-and-access.md), and a restore must not quietly undo it.
--
-- Nothing in the restored rows says which sessions were revoked later, so all of them are
-- revoked: everyone signs in again. That is the cheap side of the trade.
--
-- Deliberately NOT handled here, because a script cannot: passwords, deactivations and role
-- changes made after the backup point are lost along with the rest of that data. Revoking
-- sessions forces a fresh sign-in; it does not restore a password someone changed because the
-- old one leaked. ops/README.md says what to do about that.
--
-- Also deliberately NOT here: unused invitation tokens (identity.user_token). A restore can make a
-- link that was used after the backup point valid again, until its original expiry. It is left
-- valid because the restore also returned that account to Invited, with no password: the invitee
-- has to accept again, and nothing can reissue an invitation yet (docs/backlog.md, Deferred).
-- Expiring the tokens would lock them out with hand-written SQL as the only way back.

UPDATE identity.session          SET revoked_at = now() WHERE revoked_at IS NULL;
UPDATE platform.platform_session SET revoked_at = now() WHERE revoked_at IS NULL;
