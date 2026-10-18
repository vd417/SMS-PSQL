-- PTM: optional meeting link (e.g. a video-call URL for "Video call" mode).
ALTER TABLE "dbo"."PtmMeetings" ADD COLUMN IF NOT EXISTS "MeetingLink" text;
