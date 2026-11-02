# On-call runbook

## Rotation

The on-call rotation changes every Monday at 09:00. The primary responder must
acknowledge a page within 15 minutes. If the page is not acknowledged in that
time, it escalates to the secondary responder.

## Database failover

To promote the standby database, run the command pgctl promote on the standby
host, then confirm replication lag is zero before you redirect traffic. Record
the time of the failover in the incident channel.

## Communication

The status page is updated at least every 30 minutes during an incident.
A postmortem is written within five business days of the incident closing,
and it is reviewed in the weekly reliability meeting.
